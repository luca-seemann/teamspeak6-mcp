using System.Collections.Concurrent;

using TeamSpeak.Query.Protocol;
using TeamSpeak.Query.Transport;

namespace TeamSpeak.Query.Client;

/// <summary>The kinds of event a query client can register for.</summary>
public enum EventCategory
{
    /// <summary>Clients connecting and disconnecting, and changes to the virtual server's settings.</summary>
    Server,

    /// <summary>Channels created, edited or deleted, and clients connecting, disconnecting or moving.</summary>
    Channel,

    /// <summary>Messages sent to the whole virtual server.</summary>
    TextServer,

    /// <summary>Messages sent to the channel the event session sits in.</summary>
    TextChannel,

    /// <summary>Private messages sent to the event session itself.</summary>
    TextPrivate,

    /// <summary>Bans added or removed.</summary>
    Bans,
}

/// <summary>
/// Opens the dedicated session events arrive on.
/// </summary>
/// <param name="profile">The server.</param>
/// <param name="onSessionOpened">Registers for events; must run on every session the transport opens.</param>
/// <param name="cancellationToken">Abandons the attempt.</param>
/// <returns>The transport.</returns>
public delegate Task<IQueryTransport> EventSessionFactory(
    QueryProfile profile,
    Func<QuerySender, CancellationToken, Task> onSessionOpened,
    CancellationToken cancellationToken);

/// <summary>
/// Keeps event subscriptions, each on a query session of its own, and collects what they receive
/// into one <see cref="EventBuffer"/> per profile.
/// </summary>
/// <remarks>
/// <para>
/// Events get their own session instead of sharing the one tools use. Tool calls select other
/// virtual servers and move the shared session between channels. That would change which channel
/// messages it receives, and it must not lose the registrations. The price is one more connection
/// per profile and virtual server with a subscription.
/// </para>
/// <para>
/// Registrations belong to a session, so they are sent again on every session the transport opens,
/// including after a reconnect. Events that arrive while the session is down are lost. The number
/// of sessions opened is reported, so a caller can tell when that may have happened.
/// </para>
/// <para>
/// Everything lives in this process. Several server replicas behind a load balancer would each
/// have their own subscriptions and buffers, so events need a single instance or sticky routing.
/// </para>
/// </remarks>
public sealed class QueryEventHub : IAsyncDisposable
{
    /// <summary>How many events each profile's buffer keeps unless configured otherwise.</summary>
    public const int DefaultCapacity = 1000;

    private readonly EventSessionFactory _factory;
    private readonly int _capacity;
    private readonly ConcurrentDictionary<string, EventBuffer> _buffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string Profile, int VirtualServerId), EventSession> _sessions = new();
    private int _disposed;

    private readonly TimeSpan _reRegisterInterval;

    /// <summary>Creates a hub.</summary>
    /// <param name="profiles">The configured servers.</param>
    /// <param name="capacity">How many events each profile's buffer keeps.</param>
    /// <param name="factory">Opens event sessions. Defaults to the SSH transport; tests substitute a fake.</param>
    /// <param name="reRegisterInterval">
    /// How often a live session re-sends its registrations, to recover the silent loss a virtual
    /// server restart causes. Defaults to 30 seconds; a non-positive value turns the watchdog off,
    /// which the unit tests use so they can drive it by hand.
    /// </param>
    public QueryEventHub(
        ProfileRegistry profiles,
        int capacity = DefaultCapacity,
        EventSessionFactory? factory = null,
        TimeSpan? reRegisterInterval = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        Profiles = profiles;
        _capacity = capacity;
        _factory = factory ?? OpenAsync;
        _reRegisterInterval = reRegisterInterval ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>Gets the configured servers.</summary>
    public ProfileRegistry Profiles { get; }

    /// <summary>Gets the buffer a profile's events are collected in, creating it on first use.</summary>
    /// <param name="profileName">The profile, or <see langword="null"/> when only one is configured.</param>
    /// <returns>The buffer.</returns>
    public EventBuffer BufferFor(string? profileName)
    {
        var profile = Profiles.Resolve(profileName);
        return _buffers.GetOrAdd(profile.Name, _ => new EventBuffer(_capacity));
    }

    /// <summary>
    /// Which categories an event belongs to, following what each registration was seen to deliver.
    /// </summary>
    /// <param name="notification">The event.</param>
    /// <returns>Its categories; empty for an event this hub does not recognise.</returns>
    /// <remarks>
    /// Measured on 6.0.0-beta12.1, one registration at a time. <c>event=server</c> delivered clients
    /// entering and leaving and server edits. <c>event=channel</c> delivered channel changes, moves,
    /// and also clients entering and leaving. Text messages tell their kind by <c>targetmode</c>.
    /// </remarks>
    public static IReadOnlyList<EventCategory> CategoriesOf(QueryEvent notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        return notification.Name switch
        {
            "notifytextmessage" => (notification.Records.Count > 0 && notification.Records[0].TryGetValue("targetmode", out var mode) ? mode : null) switch
            {
                "3" => [EventCategory.TextServer],
                "2" => [EventCategory.TextChannel],
                "1" => [EventCategory.TextPrivate],
                _ => [],
            },
            "notifybanupdate" => [EventCategory.Bans],
            "notifyserveredited" => [EventCategory.Server],
            "notifycliententerview" or "notifyclientleftview" => [EventCategory.Server, EventCategory.Channel],
            "notifyclientmoved" => [EventCategory.Channel],
            var name when name.StartsWith("notifychannel", StringComparison.Ordinal) => [EventCategory.Channel],
            _ => [],
        };
    }

    /// <summary>The name TeamSpeak uses for a category in <c>servernotifyregister</c>.</summary>
    /// <param name="category">The category.</param>
    /// <returns>For example <c>textserver</c>.</returns>
    public static string WireName(EventCategory category) => category.ToString().ToLowerInvariant();

    /// <summary>
    /// Subscribes to event categories on a virtual server, opening the event session if needed.
    /// </summary>
    /// <param name="profileName">The profile, or <see langword="null"/> when only one is configured.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="categories">The categories to add. Categories already subscribed stay.</param>
    /// <param name="channelId">For <see cref="EventCategory.Channel"/>, one channel; 0 for every channel.</param>
    /// <param name="textChannelId">
    /// For <see cref="EventCategory.TextChannel"/>, the channel whose chat to receive: the event
    /// session is moved into it, and back into it after a reconnect. 0 leaves the text channel
    /// unchanged, which on a fresh session is the default channel.
    /// </param>
    /// <param name="cancellationToken">Abandons the attempt.</param>
    /// <returns>The subscription as it now stands.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the profile has no SSH access.</exception>
    /// <exception cref="QueryProtocolException">Thrown when the server refuses a registration.</exception>
    public async Task<EventSubscription> SubscribeAsync(
        string? profileName,
        int virtualServerId,
        IReadOnlyCollection<EventCategory> categories,
        int channelId = 0,
        int textChannelId = 0,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(categories);

        if (categories.Count == 0)
        {
            throw new ArgumentException("Name at least one event category.", nameof(categories));
        }

        var profile = Profiles.Resolve(profileName);
        if (!profile.CanUseSsh)
        {
            throw new InvalidOperationException(
                $"Profile '{profile.Name}' has no SSH password. TeamSpeak delivers events only over the " +
                "SSH query; the WebQuery refuses servernotifyregister with 5120.");
        }

        var buffer = BufferFor(profile.Name);
        var session = _sessions.GetOrAdd((profile.Name, virtualServerId), key => new EventSession(key.Profile, key.VirtualServerId));

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.Transport is null)
            {
                session.Replace(categories, channelId);
                session.TextChannelId = textChannelId;

                try
                {
                    session.Transport = await _factory(profile, (send, token) => RegisterAllAsync(session, send, token), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    session.Replace([], 0);
                    session.TextChannelId = 0;
                    throw;
                }

                session.Since = DateTimeOffset.UtcNow;
                session.Pump = PumpAsync(session, session.Transport, buffer, session.Stop.Token);
                session.Watchdog = WatchdogAsync(session, session.Stop.Token);
                return session.Snapshot();
            }

            var transport = session.Transport;
            var current = session.Categories();

            // A channel subscription for a different channel replaces the old one.
            if (categories.Contains(EventCategory.Channel) && current.Contains(EventCategory.Channel) && session.ChannelId != channelId)
            {
                await SendAsync(transport, Unregister(EventCategory.Channel, virtualServerId, session.ChannelId), cancellationToken).ConfigureAwait(false);
                session.Remove(EventCategory.Channel);
                current = session.Categories();
            }

            foreach (var category in categories.Distinct().Where(category => !current.Contains(category)))
            {
                await SendAsync(transport, Register(category, virtualServerId, channelId), cancellationToken).ConfigureAwait(false);
                session.Add(category, channelId);
            }

            // Moving the event session to a different text channel on an already-open session. The
            // target is stored even when the client id is not known yet, so the next reconnect (which
            // learns the id) still applies it. Passing 0 leaves the current text channel unchanged.
            if (textChannelId > 0 && textChannelId != session.TextChannelId)
            {
                session.TextChannelId = textChannelId;

                if (session.ClientId is { } self)
                {
                    await SendAsync(
                        transport,
                        new QueryCommand(
                            "clientmove",
                            new Dictionary<string, string>
                            {
                                ["clid"] = self.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                ["cid"] = textChannelId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            },
                            VirtualServerId: virtualServerId),
                        cancellationToken).ConfigureAwait(false);
                }
            }

            return session.Snapshot();
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>Ends subscriptions, closing the event session when none are left.</summary>
    /// <param name="profileName">The profile, or <see langword="null"/> when only one is configured.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="categories">The categories to end; <see langword="null"/> or empty for all of them.</param>
    /// <param name="cancellationToken">Abandons the attempt.</param>
    /// <returns>What is still subscribed, or <see langword="null"/> when nothing is.</returns>
    public async Task<EventSubscription?> UnsubscribeAsync(
        string? profileName,
        int virtualServerId,
        IReadOnlyCollection<EventCategory>? categories = null,
        CancellationToken cancellationToken = default)
    {
        var profile = Profiles.Resolve(profileName);
        if (!_sessions.TryGetValue((profile.Name, virtualServerId), out var session))
        {
            return null;
        }

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.Transport is not { } transport)
            {
                return null;
            }

            var current = session.Categories();
            var ending = categories is null || categories.Count == 0
                ? current
                : current.Where(categories.Contains).ToHashSet();

            if (ending.Count == current.Count)
            {
                // Closing the session ends every registration it held.
                await CloseAsync(session).ConfigureAwait(false);
                return null;
            }

            foreach (var category in ending)
            {
                // Remove from the set before the wire unregister, so a reconnect that re-registers
                // from the set in between cannot bring the category back after we asked to drop it.
                session.Remove(category);
                await SendAsync(transport, Unregister(category, virtualServerId, session.ChannelId), cancellationToken).ConfigureAwait(false);
            }

            return session.Snapshot();
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>Lists a profile's active subscriptions.</summary>
    /// <param name="profileName">The profile, or <see langword="null"/> when only one is configured.</param>
    /// <returns>One entry per virtual server with an open event session.</returns>
    public IReadOnlyList<EventSubscription> Subscriptions(string? profileName)
    {
        var profile = Profiles.Resolve(profileName);

        return _sessions.Values
            .Where(session => session.Transport is not null && string.Equals(session.Profile, profile.Name, StringComparison.OrdinalIgnoreCase))
            .Select(session => session.Snapshot())
            .OrderBy(subscription => subscription.VirtualServerId)
            .ToList();
    }

    /// <inheritdoc />
    /// <remarks>Closes every event session, so none is left occupying a query slot on the server.</remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        List<Exception>? failures = null;

        foreach (var session in _sessions.Values)
        {
            await session.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await CloseAsync(session).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // One event session failing to close must not leave the others open.
                (failures ??= []).Add(ex);
            }
            finally
            {
                session.Gate.Release();
            }
        }

        DisposalFailures.ThrowIfAny(failures, "Some event sessions could not be closed cleanly.");
    }

    private static async Task RegisterAllAsync(EventSession session, QuerySender send, CancellationToken cancellationToken)
    {
        // A fresh session seeds the name cache; the reader loop then keeps it current from events.
        await SendRegistrationsAsync(session, send, seedNames: true, cancellationToken).ConfigureAwait(false);

        // Counted only once registered, so a failed reconnect attempt does not look like a replacement.
        session.CountOpened();
    }

    /// <summary>Registers every category and learns the session's client id, recording the outcome.</summary>
    /// <param name="session"></param>
    /// <param name="send"></param>
    /// <param name="seedNames">
    /// Whether to reload the whole name cache from <c>clientlist</c>. Done once when a session opens;
    /// the watchdog's periodic re-registration skips it, since the reader loop keeps the cache current
    /// and a reload every pass would be steady query load that clears learned names for no gain.
    /// </param>
    /// <param name="cancellationToken"></param>
    private static async Task SendRegistrationsAsync(EventSession session, QuerySender send, bool seedNames, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var category in session.Categories())
            {
                var command = Register(category, session.VirtualServerId, session.ChannelId);
                var response = await send(command, cancellationToken).ConfigureAwait(false);
                ThrowIfRefused(command, response);
            }

            // Private messages reach the event session only when sent to its client id, which changes
            // with every session. Knowing it is not worth failing the subscription over.
            var who = await send(new QueryCommand("whoami", VirtualServerId: session.VirtualServerId), cancellationToken).ConfigureAwait(false);
            session.ClientId = who.Error.IsSuccess && who.Records.Count > 0 && who.Records[0].GetInt32("client_id") is > 0 and var clientId
                ? clientId
                : null;

            // Seed the client-name cache so events that carry only a client id can still be given a
            // nickname. Best effort: a failure here must not fail the subscription.
            if (seedNames)
            {
                var clients = await send(new QueryCommand("clientlist", VirtualServerId: session.VirtualServerId), cancellationToken).ConfigureAwait(false);
                if (clients.Error.IsSuccess)
                {
                    session.SeedNames(clients.Records);
                }
            }

            // For text-channel messages of a chosen channel, the event session's own client has to sit
            // in that channel. Re-applied here so it survives a reconnect. Best effort.
            if (session.TextChannelId > 0 && session.ClientId is { } self)
            {
                var move = await send(
                    new QueryCommand(
                        "clientmove",
                        new Dictionary<string, string>
                        {
                            ["clid"] = self.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["cid"] = session.TextChannelId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        },
                        VirtualServerId: session.VirtualServerId),
                    cancellationToken).ConfigureAwait(false);

                // Already being in the channel is success for our purpose.
                if (!move.Error.IsSuccess && move.Error.Id != QueryErrorCode.AlreadyMemberOfChannel)
                {
                    session.RecordError($"Could not move the event session into channel {session.TextChannelId}: {move.Error.Message}");
                    return;
                }
            }

            session.RecordError(null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            session.RecordError(ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Re-sends the registrations on a timer, so a subscription silently voided by a virtual server
    /// restart comes back on its own.
    /// </summary>
    /// <remarks>
    /// A stopped and restarted virtual server drops the registrations without closing the session or
    /// raising an error, so nothing else would notice. Re-registering is idempotent; the transport
    /// re-selects the virtual server first when its selection has gone stale (error 1024).
    /// </remarks>
    private async Task WatchdogAsync(EventSession session, CancellationToken stop)
    {
        if (_reRegisterInterval <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(_reRegisterInterval, stop).ConfigureAwait(false);
                await ReRegisterOnceAsync(session, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The subscription ended.
        }
    }

    /// <summary>One watchdog pass: re-register under the session gate if it is still open.</summary>
    private static async Task ReRegisterOnceAsync(EventSession session, CancellationToken cancellationToken)
    {
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.Transport is not { } transport || session.Categories().Count == 0)
            {
                return;
            }

            await SendRegistrationsAsync(session, (command, token) => transport.SendAsync(command, token), seedNames: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The failure is already recorded on the session; the next pass tries again.
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private static async Task PumpAsync(EventSession session, IQueryTransport transport, EventBuffer buffer, CancellationToken stop)
    {
        await Task.Yield();

        try
        {
            await foreach (var notification in transport.GetEventsAsync(stop).ConfigureAwait(false))
            {
                session.RecordEvent();
                buffer.Append(session.VirtualServerId, session.Track(notification));
            }
        }
        catch (OperationCanceledException)
        {
            // The subscription ended.
        }
    }

    private static async Task CloseAsync(EventSession session)
    {
        if (session.Transport is not { } transport)
        {
            return;
        }

        await session.Stop.CancelAsync().ConfigureAwait(false);

        try
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            // Even when the transport fails to close, the pump and watchdog were told to stop and the
            // session must be left empty, or a later subscribe would find a half-closed one.
            foreach (var task in new[] { session.Pump, session.Watchdog })
            {
                if (task is not null)
                {
                    try
                    {
                        await task.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when the subscription ends.
                    }
                }
            }

            session.Reset();
        }
    }

    private static async Task SendAsync(IQueryTransport transport, QueryCommand command, CancellationToken cancellationToken)
    {
        var response = await transport.SendAsync(command, cancellationToken).ConfigureAwait(false);
        ThrowIfRefused(command, response);
    }

    private static void ThrowIfRefused(QueryCommand command, QueryResponse response)
    {
        if (!response.Error.IsSuccess)
        {
            var parameters = string.Join(' ', (command.Parameters ?? new Dictionary<string, string>()).Select(pair => $"{pair.Key}={pair.Value}"));
            throw new QueryProtocolException(
                $"The server refused '{command.Name} {parameters}': {response.Error.Message}" +
                (string.IsNullOrWhiteSpace(response.Error.ExtraMessage) ? string.Empty : $", {response.Error.ExtraMessage}"));
        }
    }

    private static QueryCommand Register(EventCategory category, int virtualServerId, int channelId)
    {
        var parameters = new Dictionary<string, string> { ["event"] = WireName(category) };
        if (category == EventCategory.Channel)
        {
            parameters["id"] = channelId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new QueryCommand("servernotifyregister", parameters, VirtualServerId: virtualServerId);
    }

    /// <remarks>
    /// Measured live: <c>servernotifyunregister event=channel</c> without an id is refused with
    /// <c>1539 parameter not found</c>, while the other categories take no id.
    /// </remarks>
    private static QueryCommand Unregister(EventCategory category, int virtualServerId, int channelId)
    {
        var parameters = new Dictionary<string, string> { ["event"] = WireName(category) };
        if (category == EventCategory.Channel)
        {
            parameters["id"] = channelId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new QueryCommand("servernotifyunregister", parameters, VirtualServerId: virtualServerId);
    }

    private static async Task<IQueryTransport> OpenAsync(
        QueryProfile profile,
        Func<QuerySender, CancellationToken, Task> onSessionOpened,
        CancellationToken cancellationToken) =>
        await SshQueryTransport.ConnectAsync(profile, onSessionOpened, cancellationToken).ConfigureAwait(false);

    private sealed class EventSession(string profile, int virtualServerId)
    {
        private readonly HashSet<EventCategory> _categories = [];
        private readonly Lock _health = new();
        private readonly Dictionary<int, string> _clientNames = [];
        private int _sessionsOpened;
        private string? _lastError;
        private DateTimeOffset? _lastEventAt;
        // Written by the reconnect/registration thread, read by Snapshot; volatile for a safe hand-off.
        // Zero means "not known": client and channel ids are always positive.
        private volatile int _clientId;
        private volatile int _textChannelId;

        public string Profile { get; } = profile;

        public int VirtualServerId { get; } = virtualServerId;

        // Never disposed, for the same reason as the connection manager's gates.
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public IQueryTransport? Transport { get; set; }

        public Task? Pump { get; set; }

        public Task? Watchdog { get; set; }

        public CancellationTokenSource Stop { get; private set; } = new();

        public int ChannelId { get; private set; }

        /// <summary>The channel the event session's own client is moved into, for text-channel events; 0 for none.</summary>
        public int TextChannelId
        {
            get => _textChannelId;
            set => _textChannelId = value;
        }

        public DateTimeOffset Since { get; set; }

        /// <summary>The event session's own client id, or <see langword="null"/> when not known.</summary>
        public int? ClientId
        {
            get => _clientId == 0 ? null : _clientId;
            set => _clientId = value ?? 0;
        }

        // Copies taken under the lock: a reconnect reads the categories on the transport's own thread.
        public HashSet<EventCategory> Categories()
        {
            lock (_categories)
            {
                return [.. _categories];
            }
        }

        public void Replace(IEnumerable<EventCategory> categories, int channelId)
        {
            lock (_categories)
            {
                _categories.Clear();
                _categories.UnionWith(categories);
                ChannelId = channelId;
            }
        }

        public void Add(EventCategory category, int channelId)
        {
            lock (_categories)
            {
                _categories.Add(category);
                if (category == EventCategory.Channel)
                {
                    ChannelId = channelId;
                }
            }
        }

        public void Remove(EventCategory category)
        {
            lock (_categories)
            {
                _categories.Remove(category);
            }
        }

        public void CountOpened() => Interlocked.Increment(ref _sessionsOpened);

        /// <summary>Fills the client-name cache from a <c>clientlist</c> response.</summary>
        public void SeedNames(IReadOnlyList<QueryRecord> clients)
        {
            lock (_clientNames)
            {
                _clientNames.Clear();
                foreach (var client in clients)
                {
                    var clid = client.GetInt32("clid");
                    var name = client.GetString("client_nickname");
                    if (clid > 0 && name.Length > 0)
                    {
                        _clientNames[clid] = name;
                    }
                }
            }
        }

        /// <summary>
        /// Updates the name cache from an event and returns it with a <c>client_nickname</c> added
        /// where a record carries only a client id.
        /// </summary>
        /// <remarks>
        /// Best effort: some events (a move, a client leaving) carry only <c>clid</c>. Names are
        /// filled from what earlier events and the initial <c>clientlist</c> taught, never by asking
        /// the server per event, which would be one query per event and could flood.
        /// </remarks>
        public QueryEvent Track(QueryEvent notification)
        {
            // Learn from events that carry both a client id and a name.
            foreach (var record in notification.Records)
            {
                if (record.TryGetValue("clid", out var clidText)
                    && int.TryParse(clidText, System.Globalization.CultureInfo.InvariantCulture, out var clid)
                    && record.TryGetValue("client_nickname", out var name)
                    && !string.IsNullOrEmpty(name))
                {
                    lock (_clientNames)
                    {
                        _clientNames[clid] = name;
                    }
                }
            }

            var changed = false;
            var enriched = new List<IReadOnlyDictionary<string, string>>(notification.Records.Count);
            foreach (var record in notification.Records)
            {
                if (NameFor(record) is { } added)
                {
                    enriched.Add(new Dictionary<string, string>(record) { ["client_nickname"] = added });
                    changed = true;
                }
                else
                {
                    enriched.Add(record);
                }
            }

            // A client that just left will not be referenced again; drop it so the cache does not grow.
            if (notification.Name == "notifyclientleftview")
            {
                foreach (var record in notification.Records)
                {
                    if (record.TryGetValue("clid", out var clidText)
                        && int.TryParse(clidText, System.Globalization.CultureInfo.InvariantCulture, out var clid))
                    {
                        lock (_clientNames)
                        {
                            _clientNames.Remove(clid);
                        }
                    }
                }
            }

            return changed ? notification with { Records = enriched } : notification;
        }

        /// <summary>The cached nickname to add to a record, or null when it has one or none is known.</summary>
        private string? NameFor(IReadOnlyDictionary<string, string> record)
        {
            if (record.ContainsKey("client_nickname")
                || !record.TryGetValue("clid", out var clidText)
                || !int.TryParse(clidText, System.Globalization.CultureInfo.InvariantCulture, out var clid))
            {
                return null;
            }

            lock (_clientNames)
            {
                return _clientNames.TryGetValue(clid, out var name) ? name : null;
            }
        }

        /// <summary>Records the last event's arrival, for the health view.</summary>
        public void RecordEvent()
        {
            lock (_health)
            {
                _lastEventAt = DateTimeOffset.UtcNow;
            }
        }

        /// <summary>Records the last registration outcome: a message when it failed, <see langword="null"/> when it succeeded.</summary>
        public void RecordError(string? message)
        {
            lock (_health)
            {
                _lastError = message;
            }
        }

        public void Reset()
        {
            Replace([], 0);
            Transport = null;
            Pump = null;
            Watchdog = null;
            ClientId = null;
            TextChannelId = 0;
            Stop.Dispose();
            Stop = new CancellationTokenSource();
            Interlocked.Exchange(ref _sessionsOpened, 0);

            lock (_health)
            {
                _lastError = null;
                _lastEventAt = null;
            }

            lock (_clientNames)
            {
                _clientNames.Clear();
            }
        }

        public EventSubscription Snapshot()
        {
            lock (_health)
            {
                return new EventSubscription(
                    Profile,
                    VirtualServerId,
                    [.. Categories().Order()],
                    ChannelId,
                    TextChannelId,
                    Since,
                    Volatile.Read(ref _sessionsOpened),
                    ClientId,
                    _lastEventAt,
                    _lastError);
            }
        }
    }
}

/// <summary>What one virtual server's event session is subscribed to.</summary>
/// <param name="Profile">The profile.</param>
/// <param name="VirtualServerId">The virtual server.</param>
/// <param name="Categories">The subscribed categories.</param>
/// <param name="ChannelId">The channel the channel category is limited to; 0 for every channel.</param>
/// <param name="TextChannelId">The channel the event session sits in for text-channel messages; 0 for the default channel.</param>
/// <param name="Since">When the event session was opened.</param>
/// <param name="SessionsOpened">
/// How many sessions have carried the subscription. Anything above one means the connection was
/// replaced, and events that arrived in between were lost.
/// </param>
/// <param name="ClientId">
/// The event session's client id on the virtual server, where private messages to it must be sent;
/// absent when it could not be read. It changes whenever the session is replaced.
/// </param>
/// <param name="LastEventAt">When the last event arrived on this session, or <see langword="null"/> if none has.</param>
/// <param name="LastError">
/// The last registration failure, or <see langword="null"/> when the last registration succeeded. A
/// non-null value means the subscription is currently not delivering, for example because the virtual
/// server is stopped.
/// </param>
public sealed record EventSubscription(
    string Profile,
    int VirtualServerId,
    IReadOnlyList<EventCategory> Categories,
    int ChannelId,
    int TextChannelId,
    DateTimeOffset Since,
    int SessionsOpened,
    int? ClientId,
    DateTimeOffset? LastEventAt,
    string? LastError);