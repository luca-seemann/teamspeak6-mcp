using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

using Renci.SshNet;

using TeamSpeak.Query.Client;
using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Query.Transport;

/// <summary>
/// Talks to the ServerQuery interface over one long-lived SSH session.
/// </summary>
/// <remarks>
/// <para>
/// The session is deliberately shared by every caller rather than opened per command: reconnecting
/// repeatedly is what trips the server's flood protection, and connections are punished far more
/// harshly than commands. A <see cref="FloodGuard"/> serialises callers and keeps a gap between
/// commands; a <see cref="ConnectionThrottle"/> does the same for connections.
/// </para>
/// <para>
/// Three details of the real server drive this implementation and appear nowhere in its
/// documentation. It refuses pseudo-terminal requests, so the channel is opened without one. It
/// emits no prompt at all — the greeting line is <c>TS3</c> — so responses can only be framed on
/// the trailing <c>error id=</c> line. And <c>ShellStream</c> returns a read count of zero simply
/// because nothing has arrived yet, which is not end-of-stream.
/// </para>
/// </remarks>
public sealed class SshQueryTransport : IQueryTransport
{
    private readonly QueryProfile _profile;
    private readonly FloodGuard _guard;
    private readonly ReconnectPolicy _reconnect;
    private readonly TimeSpan _commandTimeout;
    private readonly TimeSpan _keepAliveInterval;
    private readonly int _defaultVirtualServerId;

    private readonly Channel<QueryEvent> _events = Channel.CreateBounded<QueryEvent>(
        new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly SemaphoreSlim _pendingSlot = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    private SshClient _client;
    // Volatile because the reader loop compares against it to notice it has been replaced.
    private volatile ShellStream _shell;
    private Task _readerLoop;
    private Timer? _keepAlive;
    private int _keepAliveRunning;

    private TaskCompletionSource<QueryResponse>? _pending;

    // Guards the hand-over between a command becoming pending and a session being found lost or being
    // replaced, which happen on different threads; see OnSessionLost and ExchangeOnceAsync.
    private readonly Lock _sessionGate = new();

    private int _disposed;
    private StringBuilder _pendingText = new();
    private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;

    // What the server session currently has selected, zero for nothing. Only read or written while
    // holding _pendingSlot, so it always describes the session the next command will run on.
    private int _selectedVirtualServerId;

    // Set when a command was abandoned after it went out. The protocol carries no request ids, so a
    // late answer to it would be indistinguishable from the next command's; the session is replaced
    // before anything else is sent on it.
    private volatile bool _desynchronized;

    // Run on every fresh session before anything else is sent on it: the first one and each
    // replacement. Registrations such as servernotifyregister live and die with a session.
    private readonly Func<QuerySender, CancellationToken, Task>? _onSessionOpened;

    private SshQueryTransport(
        QueryProfile profile,
        SshClient client,
        ShellStream shell,
        Func<QuerySender, CancellationToken, Task>? onSessionOpened)
    {
        _profile = profile;
        _client = client;
        _shell = shell;
        _onSessionOpened = onSessionOpened;
        WatchSession(client, shell);
        _guard = new FloodGuard(profile.CommandInterval);
        _reconnect = new ReconnectPolicy();
        _commandTimeout = profile.CommandTimeout;
        _defaultVirtualServerId = profile.DefaultVirtualServerId;

        // The server drops a session after 25 to 30 seconds without a command (measured), so this
        // stays below that. One cheap command every few seconds is nothing next to the flood budget.
        _keepAliveInterval = profile.KeepAliveInterval;

        _readerLoop = Task.Run(() => ReadLoopAsync(_shutdown.Token));
    }

    /// <inheritdoc />
    /// <remarks>Always <see langword="true"/>: SSH is the only interface that delivers events.</remarks>
    public bool SupportsEvents => true;

    /// <inheritdoc />
    public bool HoldsSession => true;

    /// <inheritdoc />
    /// <remarks>
    /// The sequence holds the session slot throughout, which is what keeps other callers' commands
    /// — including a <c>use</c> for another virtual server — out of it. A session that falls out of
    /// step during the sequence fails the rest of it; the next ordinary command replaces the session.
    /// </remarks>
    public async Task<T> RunExclusiveAsync<T>(Func<QuerySender, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        await _pendingSlot.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await work(SendHoldingSlotAsync).ConfigureAwait(false);
        }
        finally
        {
            _pendingSlot.Release();
        }
    }

    /// <summary>Opens a session against a profile.</summary>
    /// <param name="profile">The server to connect to. Must have a password.</param>
    /// <param name="cancellationToken">Abandons the connection attempt.</param>
    /// <returns>A connected transport.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the profile has no password.</exception>
    public static Task<SshQueryTransport> ConnectAsync(
        QueryProfile profile,
        CancellationToken cancellationToken = default) =>
        ConnectAsync(profile, onSessionOpened: null, cancellationToken);

    /// <summary>Opens a session against a profile, preparing every session it opens.</summary>
    /// <param name="profile">The server to connect to. Must have a password.</param>
    /// <param name="onSessionOpened">
    /// Runs on each fresh session before any other command is sent on it: once now, and again after
    /// every reconnect. It sends through the sender it is given. When it fails, connecting fails, and
    /// a reconnect attempt counts as failed.
    /// </param>
    /// <param name="cancellationToken">Abandons the connection attempt.</param>
    /// <returns>A connected transport.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the profile has no password.</exception>
    public static async Task<SshQueryTransport> ConnectAsync(
        QueryProfile profile,
        Func<QuerySender, CancellationToken, Task>? onSessionOpened,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (!profile.CanUseSsh)
        {
            throw new InvalidOperationException(
                $"Profile '{profile.Name}' has no password, which the SSH interface requires.");
        }

        var (client, shell) = await OpenAsync(profile, cancellationToken).ConfigureAwait(false);
        var transport = new SshQueryTransport(profile, client, shell, onSessionOpened);

        if (onSessionOpened is not null)
        {
            try
            {
                await transport.RunExclusiveAsync(
                    async send =>
                    {
                        await onSessionOpened(send, cancellationToken).ConfigureAwait(false);
                        return true;
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transport.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        transport.StartKeepAlive();
        return transport;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A <c>use</c> is sent first only when the session has a different virtual server selected,
    /// and it is sent under the same hold of the session as the command itself, so no other
    /// caller's command can land in between.
    /// </remarks>
    public async Task<QueryResponse> SendAsync(
        QueryCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        return await SendOnceAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryEvent> GetEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var notification in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return notification;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_keepAlive is not null)
        {
            await _keepAlive.DisposeAsync().ConfigureAwait(false);
        }

        await SayGoodbyeAsync().ConfigureAwait(false);

        // ShellStream.ReadAsync does not honour a cancellation token, so cancelling alone leaves
        // the reader blocked. Disposing the stream is what actually unblocks it.
        _shell.Dispose();

        try
        {
            await _readerLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // A stuck reader must not block disposal.
        }

        _events.Writer.TryComplete();
        _client.Dispose();
        _shutdown.Dispose();
        _pendingSlot.Dispose();
        _guard.Dispose();
    }

    private static async Task<(SshClient Client, ShellStream Shell)> OpenAsync(
        QueryProfile profile,
        CancellationToken cancellationToken)
    {
        // Connections, not commands, are what earn an IP-level block from this server.
        await ConnectionThrottle.Shared.WaitForTurnAsync(profile.Host, cancellationToken).ConfigureAwait(false);

        // A guest has no password, and the server accepts any for that user, so it sends an empty one.
        var password = profile.Password ?? string.Empty;

        var connectionInfo = new ConnectionInfo(
            profile.Host,
            profile.SshPort,
            profile.Username,
            new PasswordAuthenticationMethod(profile.Username, password))
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        var verifier = profile.HostKeyFingerprint is { Length: > 0 } pinned
            ? new PinnedHostKey(pinned, $"TeamSpeak:Profiles:{profile.Name}:HostKeyFingerprint")
            : profile.HostKeyVerifier;

        var client = new SshClient(connectionInfo);
        HostKeyVerdict? refusal = null;

        if (verifier is not null)
        {
            // Runs during the key exchange, before the password is sent.
            client.HostKeyReceived += (_, e) =>
            {
                var verdict = verifier.Verify(profile.Host, profile.SshPort, e.HostKeyName, "SHA256:" + e.FingerPrintSHA256);
                e.CanTrust = verdict.Trusted;
                refusal = verdict.Trusted ? null : verdict;
            };
        }

        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (refusal is { Reason: { } reason })
        {
            client.Dispose();
            throw new SshHostKeyMismatchException(reason, ex);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        ShellStream? shell = null;
        try
        {
            // The server rejects a PTY request, so the terminal-less channel is mandatory.
            shell = client.CreateShellStreamNoTerminal(bufferSize: 256 * 1024);

            await DiscardGreetingAsync(shell, cancellationToken).ConfigureAwait(false);

            return (client, shell);
        }
        catch
        {
            shell?.Dispose();
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads the greeting off a fresh session before any reader or command can see it.
    /// </summary>
    /// <remarks>
    /// The greeting is a <c>TS3</c> line and a <c>Welcome to the TeamSpeak ServerQuery interface</c>
    /// line, with no status line to frame on. Merely waiting for it and leaving it to the reader was
    /// wrong after a reconnect: the next command is sent at once, so the greeting was taken as the
    /// first lines of its response.
    /// </remarks>
    private static async Task DiscardGreetingAsync(ShellStream shell, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));

        var received = new StringBuilder();
        var buffer = new byte[4096];

        try
        {
            while (true)
            {
                var text = received.ToString();

                var welcome = text.IndexOf("Welcome", StringComparison.Ordinal);
                if (welcome >= 0 && text.IndexOf('\n', welcome) >= 0)
                {
                    return;
                }

                // A server that refuses the session, for example after a flood block, says so with a
                // status line instead of greeting.
                var refusal = text.IndexOf("error id=", StringComparison.Ordinal);
                if (refusal >= 0 && text.IndexOf('\n', refusal) >= 0)
                {
                    throw new QueryProtocolException(
                        $"The server refused the query session: {text[refusal..].Trim()}");
                }

                // As in the reader: zero bytes means nothing has arrived yet, not end-of-stream.
                if (!shell.DataAvailable)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token).ConfigureAwait(false);
                    continue;
                }

                var read = shell.Read(buffer, 0, buffer.Length);
                if (read > 0)
                {
                    received.Append(Encoding.UTF8.GetString(buffer, 0, read));
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new QueryProtocolException(
                "The server accepted the SSH connection but sent no ServerQuery greeting within 10 seconds. " +
                "Check that the SSH query interface is enabled.");
        }
    }

    /// <summary>
    /// Fails the command waiting on a session as soon as that session is lost.
    /// </summary>
    /// <remarks>
    /// Nothing else would end the wait early: the reader only ever sees that no data arrives, so a
    /// connection reset in the middle of a command used to surface after the whole command timeout
    /// (30 seconds, measured through a relay that reset the connection right after the command went
    /// out). SSH.NET learns of a reset at once and says so through these events.
    /// </remarks>
    private void WatchSession(SshClient client, ShellStream shell)
    {
        client.ErrorOccurred += (_, e) => OnSessionLost(shell, e.Exception);
        shell.ErrorOccurred += (_, e) => OnSessionLost(shell, e.Exception);
        shell.Closed += (_, _) => OnSessionLost(shell, cause: null);
    }

    private void OnSessionLost(ShellStream shell, Exception? cause)
    {
        lock (_sessionGate)
        {
            // A session being replaced or shut down on purpose raises the same events; they mean
            // nothing for the session that follows it. The check and the mark share the lock with the
            // swap in EnsureConnectedAsync, so a late event cannot mark the replacement.
            if (_shutdown.IsCancellationRequested || !ReferenceEquals(shell, _shell))
            {
                return;
            }

            _desynchronized = true;

            // Completions run asynchronously, so failing the command here runs none of its code under the lock.
            _pending?.TrySetException(cause is null
                ? new QueryProtocolException(SessionLostMessage)
                : new QueryProtocolException(SessionLostMessage, cause));
        }
    }

    /// <summary>
    /// Gets whether the current SSH client is connected, treating a client already disposed as not.
    /// </summary>
    /// <remarks>
    /// A reconnect attempt disposes the old client before opening the next one, and a failed attempt
    /// leaves the disposed client in place, whose <c>IsConnected</c> throws rather than answering.
    /// </remarks>
    private bool ClientConnected
    {
        get
        {
            try
            {
                return _client.IsConnected;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    private const string SessionLostMessage =
        "The connection to the TeamSpeak server was lost before it answered. " +
        "It is reopened on the next command; whether this one took effect is unknown.";

    /// <summary>
    /// Ends the session with <c>quit</c>, so the server lets go of it at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured on 6.0.0-beta12.1: a session closed without <c>quit</c> stayed on the virtual server as a
    /// query client for 30 seconds and then left with <c>reasonid=3</c>, connection lost. With <c>quit</c>
    /// it left at once with <c>reasonid=8</c>.
    /// </para>
    /// <para>
    /// Best effort and bounded: a session that is already gone, out of step, busy for more than a second
    /// with a command that has not answered, or held back by the flood guard for more than a second is
    /// simply closed.
    /// </para>
    /// </remarks>
    private async Task SayGoodbyeAsync()
    {
        if (!ClientConnected || _desynchronized)
        {
            return;
        }

        if (!await _pendingSlot.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            using var lease = await _guard.AcquireAsync(deadline.Token).ConfigureAwait(false);

            await _shell.WriteAsync("quit\n"u8.ToArray(), deadline.Token).ConfigureAwait(false);
            await _shell.FlushAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException
                                       or InvalidOperationException or Renci.SshNet.Common.SshException)
        {
            // The session is closed below either way.
        }
        finally
        {
            _pendingSlot.Release();
        }
    }

    private void StartKeepAlive()
    {
        // Checked several times per interval, so a dropped session is noticed within seconds
        // rather than a whole interval later.
        var check = TimeSpan.FromSeconds(Math.Clamp(_keepAliveInterval.TotalSeconds / 3, 1, 5));

        _keepAlive = new Timer(
            _ => _ = KeepAliveTickAsync(),
            state: null,
            check,
            check);
    }

    /// <summary>
    /// Sends a cheap command when the session has been idle, so the server does not reap it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured on 6.0.0-beta12.1, and unchanged on 6.0.0-beta13: the server closed a query session
    /// after 25 to 30 seconds without a command, although its documentation speaks of 300. SSH-level
    /// keepalive packets did not stop it, so this has to be a real command.
    /// </para>
    /// <para>
    /// A session found disconnected is reopened here at once instead of on the next command. For a
    /// session that only receives events there is no next command, and every event in between would
    /// be lost.
    /// </para>
    /// </remarks>
    private async Task KeepAliveTickAsync()
    {
        if (_shutdown.IsCancellationRequested
            || (ClientConnected && !_desynchronized && DateTimeOffset.UtcNow - _lastActivity < _keepAliveInterval))
        {
            return;
        }

        // A reconnect with backoff can outlast several ticks; one at a time is enough.
        if (Interlocked.Exchange(ref _keepAliveRunning, 1) != 0)
        {
            return;
        }

        try
        {
            await SendAsync(new QueryCommand("version"), _shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed keepalive is not worth surfacing; the next tick or command tries again.
        }
        finally
        {
            Interlocked.Exchange(ref _keepAliveRunning, 0);
        }
    }

    /// <summary>
    /// Reopens the session if it has dropped, backing off between attempts.
    /// </summary>
    /// <remarks>
    /// A reconnect loop without backoff is itself what the flood protection punishes, so attempts
    /// are spaced and capped. A fresh session has nothing selected, so the selection is forgotten
    /// and the next scoped command selects its virtual server again. A session that is still
    /// connected but out of step with its responses is replaced the same way.
    /// </remarks>
    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (ClientConnected && !_desynchronized)
        {
            return;
        }

        await _pendingSlot.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ClientConnected && !_desynchronized)
            {
                return;
            }

            for (var attempt = 1; ; attempt++)
            {
                var delay = _reconnect.DelayBefore(attempt);
                if (delay is null)
                {
                    throw new QueryProtocolException(
                        $"Could not reconnect to '{_profile.Host}' after {_reconnect.MaxAttempts} attempts.");
                }

                await Task.Delay(delay.Value, cancellationToken).ConfigureAwait(false);

                try
                {
                    _shell.Dispose();
                    _client.Dispose();

                    var (client, shell) = await OpenAsync(_profile, cancellationToken).ConfigureAwait(false);
                    lock (_sessionGate)
                    {
                        _client = client;
                        _shell = shell;
                        _selectedVirtualServerId = 0;
                        _desynchronized = false;
                    }

                    // A loss before this subscription is still seen: the next check finds the client disconnected.
                    WatchSession(client, shell);
                    _readerLoop = Task.Run(() => ReadLoopAsync(_shutdown.Token), CancellationToken.None);

                    // Still holding the slot, so the session is prepared before any caller's command.
                    // SendHoldingSlotAsync refuses while desynchronized, so the flag is cleared above
                    // first; if preparation fails, mark it desynchronized again so the next command
                    // reconnects and re-prepares rather than trusting a session with no registrations.
                    if (_onSessionOpened is not null)
                    {
                        try
                        {
                            await _onSessionOpened(SendHoldingSlotAsync, cancellationToken).ConfigureAwait(false);
                        }
                        catch
                        {
                            _desynchronized = true;
                            throw;
                        }
                    }

                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not SshHostKeyMismatchException)
                {
                    // Fall through to the next attempt. A refused host key is not retried: it will
                    // not change, and every further connection counts towards a flood block.
                }
            }
        }
        finally
        {
            _pendingSlot.Release();
        }
    }

    private async Task<QueryResponse> SendOnceAsync(QueryCommand command, CancellationToken cancellationToken)
    {
        // The slot is taken before any flood guard lease, everywhere, so the two can never be
        // acquired in opposite orders by different callers.
        await _pendingSlot.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SendHoldingSlotAsync(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pendingSlot.Release();
        }
    }

    /// <summary>Selects the command's virtual server if needed and sends it. The caller holds the slot.</summary>
    private async Task<QueryResponse> SendHoldingSlotAsync(QueryCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Only reachable inside an exclusive sequence: outside one, EnsureConnectedAsync has already
        // replaced a session in this state.
        if (_desynchronized)
        {
            throw new QueryProtocolException(
                "The query session fell out of step with its responses; it is replaced before the next command.");
        }

        try
        {
            var scoped = !QueryCommandScope.IsInstanceWide(command.Name);
            var response = await SelectAndExchangeAsync(command, scoped, cancellationToken).ConfigureAwait(false);

            // A stopped and restarted virtual server leaves the old selection stale: the command is
            // refused with 1024 without having run. Forget the selection, which forces a fresh use,
            // and try the command once more. Safe because a rejected command did not take effect.
            if (scoped && response.Error.Id == QueryErrorCode.InvalidServerId && _selectedVirtualServerId != 0)
            {
                _selectedVirtualServerId = 0;
                response = await SelectAndExchangeAsync(command, scoped, cancellationToken).ConfigureAwait(false);
            }

            TrackSelection(command, response);
            return response;
        }
        catch
        {
            // After a timeout or a broken write it is unknown what the session has selected, so
            // the next scoped command must select again rather than trust stale state.
            _selectedVirtualServerId = 0;
            throw;
        }
    }

    /// <summary>Selects the command's virtual server if needed, then sends the command.</summary>
    private async Task<QueryResponse> SelectAndExchangeAsync(QueryCommand command, bool scoped, CancellationToken cancellationToken)
    {
        if (scoped)
        {
            var target = command.VirtualServerId ?? _defaultVirtualServerId;

            if (target != _selectedVirtualServerId)
            {
                var use = await ExchangeAsync(UseCommand(target), cancellationToken).ConfigureAwait(false);

                if (!use.Error.IsSuccess)
                {
                    // The failed selection is the answer: running the command anyway would
                    // address whatever happened to be selected before.
                    _selectedVirtualServerId = 0;
                    return use;
                }

                _selectedVirtualServerId = target;
            }
        }

        return await ExchangeAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps the recorded selection true when a caller changes it with a command of its own.
    /// </summary>
    private void TrackSelection(QueryCommand command, QueryResponse response)
    {
        if (!response.Error.IsSuccess)
        {
            return;
        }

        if (string.Equals(command.Name, "use", StringComparison.OrdinalIgnoreCase))
        {
            _selectedVirtualServerId =
                command.Parameters is not null
                && command.Parameters.TryGetValue("sid", out var sid)
                && int.TryParse(sid, System.Globalization.CultureInfo.InvariantCulture, out var id)
                    ? id
                    : 0;
        }
        else if (command.Name.ToLowerInvariant() is "logout" or "permreset" or "serversnapshotdeploy")
        {
            // A reset or a deployment rebuilds the virtual server underneath the session, which may
            // no longer be where it was. Selecting again costs one command and is always correct.
            _selectedVirtualServerId = 0;
        }
        else if (command.Name.ToLowerInvariant() is "serverstop" or "serverdelete"
                 && command.Parameters is not null
                 && command.Parameters.TryGetValue("sid", out var stopped)
                 && int.TryParse(stopped, System.Globalization.CultureInfo.InvariantCulture, out var stoppedId)
                 && stoppedId == _selectedVirtualServerId)
        {
            // Stopping or deleting the selected virtual server drops the session from it.
            _selectedVirtualServerId = 0;
        }
    }

    private static QueryCommand UseCommand(int virtualServerId) =>
        new("use", new Dictionary<string, string>
        {
            ["sid"] = virtualServerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

    /// <summary>Sends one command and waits for its response. The caller holds the slot.</summary>
    private async Task<QueryResponse> ExchangeAsync(QueryCommand command, CancellationToken cancellationToken)
    {
        var response = await ExchangeOnceAsync(command, cancellationToken).ConfigureAwait(false);

        // The server states how long to wait; honouring it is what keeps a rejection from
        // escalating into an IP-level block.
        if (response.Error.IsFlooding)
        {
            _guard.PenaliseFor(response.Error.RetryAfter);
            response = await ExchangeOnceAsync(command, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    private async Task<QueryResponse> ExchangeOnceAsync(QueryCommand command, CancellationToken cancellationToken)
    {
        using var lease = await _guard.AcquireAsync(cancellationToken).ConfigureAwait(false);

        var completion = new TaskCompletionSource<QueryResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _pendingText = new StringBuilder();

        // OnSessionLost marks the session and fails whatever is pending under the same lock, so either
        // it sees this command or this command sees the mark. A session lost just before the command
        // therefore cannot leave it waiting out the whole timeout.
        bool lost;
        lock (_sessionGate)
        {
            lost = _desynchronized;
            _pending = lost ? null : completion;
        }

        if (lost)
        {
            throw new QueryProtocolException(SessionLostMessage);
        }

        var line = Encoding.UTF8.GetBytes(QueryCommandSerializer.ToWireLine(command) + '\n');
        var sending = false;

        try
        {
            sending = true;
            await _shell.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            await _shell.FlushAsync(cancellationToken).ConfigureAwait(false);

            using var registration = cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));

            // A response that never arrives must surface as an error rather than hang the caller;
            // an MCP tool call has a person waiting at the other end of it.
            var response = await completion.Task
                .WaitAsync(command.Timeout ?? _commandTimeout, cancellationToken)
                .ConfigureAwait(false);

            _lastActivity = DateTimeOffset.UtcNow;
            return response;
        }
        catch when (sending)
        {
            // Timed out, cancelled or broken after at least part of the line went out. The server may
            // still answer, and that answer would complete whichever command is sent next.
            _desynchronized = true;
            throw;
        }
        finally
        {
            _pending = null;
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var carry = new StringBuilder();
        var shell = _shell;

        // A reconnect replaces the shell and starts a new reader. This one must stop at once: a
        // disposed ShellStream can still hand out buffered bytes, and those belong to the abandoned
        // session, not to whatever command is pending on the new one.
        while (!cancellationToken.IsCancellationRequested && ReferenceEquals(shell, _shell))
        {
            int read;
            try
            {
                // A read count of zero means nothing has arrived yet, not end-of-stream, so
                // DataAvailable is the only reliable way to tell the two apart.
                if (!shell.DataAvailable)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                read = shell.Read(buffer, 0, buffer.Length);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }

            if (read <= 0)
            {
                continue;
            }

            carry.Append(Encoding.UTF8.GetString(buffer, 0, read));
            DrainLines(carry, shell);
        }
    }

    private void DrainLines(StringBuilder carry, ShellStream shell)
    {
        var text = carry.ToString();
        int newline;

        while ((newline = text.IndexOf('\n', StringComparison.Ordinal)) >= 0
               && ReferenceEquals(shell, _shell))
        {
            // Only carriage returns are removed, never spaces. A help page quotes example responses
            // indented by two spaces, status line included, and trimming them would end the response
            // in the middle of the page and hand the rest to the next command.
            var line = text[..newline].Trim('\r');
            text = text[(newline + 1)..];

            Dispatch(line);
        }

        carry.Clear();
        carry.Append(text);
    }

    private void Dispatch(string line)
    {
        // Notifications arrive unsolicited and can interleave with a response, so they are routed
        // away before anything is treated as response text.
        if (line.StartsWith("notify", StringComparison.Ordinal))
        {
            _events.Writer.TryWrite(ParseNotification(line));
            return;
        }

        var pending = _pending;
        if (pending is null || (line.Length == 0 && _pendingText.Length == 0))
        {
            return;
        }

        _pendingText.Append(line).Append('\n');

        if (line.StartsWith("error ", StringComparison.Ordinal))
        {
            pending.TrySetResult(QueryResponseParser.Parse(_pendingText.ToString()));
        }
    }

    private static QueryEvent ParseNotification(string line)
    {
        var space = line.IndexOf(' ', StringComparison.Ordinal);
        var name = space < 0 ? line : line[..space];
        var payload = space < 0 ? string.Empty : line[(space + 1)..];

        // Reuse the response parser by giving the payload the status line it expects.
        var parsed = QueryResponseParser.Parse(payload + "\nerror id=0 msg=ok\n");

        return new QueryEvent(name, parsed.Records, DateTimeOffset.UtcNow);
    }
}