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

    /// <summary>Creates a hub.</summary>
    /// <param name="profiles">The configured servers.</param>
    /// <param name="capacity">How many events each profile's buffer keeps.</param>
    /// <param name="factory">Opens event sessions. Defaults to the SSH transport; tests substitute a fake.</param>
    public QueryEventHub(ProfileRegistry profiles, int capacity = DefaultCapacity, EventSessionFactory? factory = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        Profiles = profiles;
        _capacity = capacity;
        _factory = factory ?? OpenAsync;
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
    /// <param name="cancellationToken">Abandons the attempt.</param>
    /// <returns>The subscription as it now stands.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the profile has no SSH access.</exception>
    /// <exception cref="QueryProtocolException">Thrown when the server refuses a registration.</exception>
    public async Task<EventSubscription> SubscribeAsync(
        string? profileName,
        int virtualServerId,
        IReadOnlyCollection<EventCategory> categories,
        int channelId = 0,
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

                try
                {
                    session.Transport = await _factory(profile, (send, token) => RegisterAllAsync(session, send, token), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    session.Replace([], 0);
                    throw;
                }

                session.Since = DateTimeOffset.UtcNow;
                session.Pump = PumpAsync(session, session.Transport, buffer, session.Stop.Token);
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
                await SendAsync(transport, Unregister(category, virtualServerId, session.ChannelId), cancellationToken).ConfigureAwait(false);
                session.Remove(category);
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

        foreach (var session in _sessions.Values)
        {
            await session.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await CloseAsync(session).ConfigureAwait(false);
            }
            finally
            {
                session.Gate.Release();
            }
        }
    }

    private static async Task RegisterAllAsync(EventSession session, QuerySender send, CancellationToken cancellationToken)
    {
        foreach (var category in session.Categories())
        {
            var command = Register(category, session.VirtualServerId, session.ChannelId);
            var response = await send(command, cancellationToken).ConfigureAwait(false);
            ThrowIfRefused(command, response);
        }

        // Private messages reach the event session only when sent to its client id, which changes with
        // every session. Knowing it is not worth failing the subscription over.
        var who = await send(new QueryCommand("whoami", VirtualServerId: session.VirtualServerId), cancellationToken).ConfigureAwait(false);
        session.ClientId = who.Error.IsSuccess && who.Records.Count > 0 && who.Records[0].GetInt32("client_id") is > 0 and var clientId
            ? clientId
            : null;

        // Counted only once registered, so a failed reconnect attempt does not look like a replacement.
        session.CountOpened();
    }

    private static async Task PumpAsync(EventSession session, IQueryTransport transport, EventBuffer buffer, CancellationToken stop)
    {
        await Task.Yield();

        try
        {
            await foreach (var notification in transport.GetEventsAsync(stop).ConfigureAwait(false))
            {
                buffer.Append(session.VirtualServerId, notification);
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
        await transport.DisposeAsync().ConfigureAwait(false);

        if (session.Pump is { } pump)
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when the subscription ends.
            }
        }

        session.Reset();
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
        private int _sessionsOpened;

        public string Profile { get; } = profile;

        public int VirtualServerId { get; } = virtualServerId;

        // Never disposed, for the same reason as the connection manager's gates.
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public IQueryTransport? Transport { get; set; }

        public Task? Pump { get; set; }

        public CancellationTokenSource Stop { get; private set; } = new();

        public int ChannelId { get; private set; }

        public DateTimeOffset Since { get; set; }

        public int? ClientId { get; set; }

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

        public void Reset()
        {
            Replace([], 0);
            Transport = null;
            Pump = null;
            ClientId = null;
            Stop.Dispose();
            Stop = new CancellationTokenSource();
            Interlocked.Exchange(ref _sessionsOpened, 0);
        }

        public EventSubscription Snapshot() =>
            new(Profile, VirtualServerId, [.. Categories().Order()], ChannelId, Since, Volatile.Read(ref _sessionsOpened), ClientId);
    }
}

/// <summary>What one virtual server's event session is subscribed to.</summary>
/// <param name="Profile">The profile.</param>
/// <param name="VirtualServerId">The virtual server.</param>
/// <param name="Categories">The subscribed categories.</param>
/// <param name="ChannelId">The channel the channel category is limited to; 0 for every channel.</param>
/// <param name="Since">When the event session was opened.</param>
/// <param name="SessionsOpened">
/// How many sessions have carried the subscription. Anything above one means the connection was
/// replaced, and events that arrived in between were lost.
/// </param>
/// <param name="ClientId">
/// The event session's client id on the virtual server, where private messages to it must be sent;
/// absent when it could not be read. It changes whenever the session is replaced.
/// </param>
public sealed record EventSubscription(
    string Profile,
    int VirtualServerId,
    IReadOnlyList<EventCategory> Categories,
    int ChannelId,
    DateTimeOffset Since,
    int SessionsOpened,
    int? ClientId);