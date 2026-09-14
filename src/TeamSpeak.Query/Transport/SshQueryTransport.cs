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
    private ShellStream _shell;
    private Task _readerLoop;
    private Timer? _keepAlive;

    private TaskCompletionSource<QueryResponse>? _pending;
    private StringBuilder _pendingText = new();
    private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;

    // What the server session currently has selected, zero for nothing. Only read or written while
    // holding _pendingSlot, so it always describes the session the next command will run on.
    private int _selectedVirtualServerId;

    private SshQueryTransport(QueryProfile profile, SshClient client, ShellStream shell)
    {
        _profile = profile;
        _client = client;
        _shell = shell;
        _guard = new FloodGuard(profile.CommandInterval);
        _reconnect = new ReconnectPolicy();
        _commandTimeout = profile.CommandTimeout;
        _defaultVirtualServerId = profile.DefaultVirtualServerId;

        // Comfortably inside the server's 300 second query timeout, and rare enough that the
        // keepalive traffic is irrelevant next to the flood budget.
        _keepAliveInterval = TimeSpan.FromSeconds(120);

        _readerLoop = Task.Run(() => ReadLoopAsync(_shutdown.Token));
    }

    /// <inheritdoc />
    /// <remarks>Always <see langword="true"/>: SSH is the only interface that delivers events.</remarks>
    public bool SupportsEvents => true;

    /// <summary>Opens a session against a profile.</summary>
    /// <param name="profile">The server to connect to. Must have a password.</param>
    /// <param name="cancellationToken">Abandons the connection attempt.</param>
    /// <returns>A connected transport.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the profile has no password.</exception>
    public static async Task<SshQueryTransport> ConnectAsync(
        QueryProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (!profile.CanUseSsh)
        {
            throw new InvalidOperationException(
                $"Profile '{profile.Name}' has no password, which the SSH interface requires.");
        }

        var (client, shell) = await OpenAsync(profile, cancellationToken).ConfigureAwait(false);
        var transport = new SshQueryTransport(profile, client, shell);
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
        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_keepAlive is not null)
        {
            await _keepAlive.DisposeAsync().ConfigureAwait(false);
        }

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

        var connectionInfo = new ConnectionInfo(
            profile.Host,
            profile.SshPort,
            profile.Username,
            new PasswordAuthenticationMethod(profile.Username, profile.Password!))
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        var client = new SshClient(connectionInfo);
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // The server rejects a PTY request, so the terminal-less channel is mandatory.
            var shell = client.CreateShellStreamNoTerminal(bufferSize: 256 * 1024);

            // The greeting is two lines and carries no status line, so it cannot be framed like a
            // response; give it a moment and let the reader discard it.
            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);

            return (client, shell);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private void StartKeepAlive() =>
        _keepAlive = new Timer(
            _ => _ = KeepAliveTickAsync(),
            state: null,
            _keepAliveInterval,
            _keepAliveInterval);

    /// <summary>
    /// Sends a cheap command when the session has been idle, so the server does not reap it.
    /// </summary>
    /// <remarks>
    /// The server closes idle query sessions after <c>--query-timeout</c>, 300 seconds by default.
    /// SSH-level keepalives do not count as query activity, so this has to be a real command.
    /// </remarks>
    private async Task KeepAliveTickAsync()
    {
        if (_shutdown.IsCancellationRequested
            || DateTimeOffset.UtcNow - _lastActivity < _keepAliveInterval)
        {
            return;
        }

        try
        {
            await SendAsync(new QueryCommand("version"), _shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed keepalive is not worth surfacing; the next real command reconnects.
        }
    }

    /// <summary>
    /// Reopens the session if it has dropped, backing off between attempts.
    /// </summary>
    /// <remarks>
    /// A reconnect loop without backoff is itself what the flood protection punishes, so attempts
    /// are spaced and capped. A fresh session has nothing selected, so the selection is forgotten
    /// and the next scoped command selects its virtual server again.
    /// </remarks>
    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client.IsConnected)
        {
            return;
        }

        await _pendingSlot.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client.IsConnected)
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
                    _client = client;
                    _shell = shell;
                    _selectedVirtualServerId = 0;
                    _readerLoop = Task.Run(() => ReadLoopAsync(_shutdown.Token), CancellationToken.None);
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Fall through to the next attempt.
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
            if (!QueryCommandScope.IsInstanceWide(command.Name))
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

            var response = await ExchangeAsync(command, cancellationToken).ConfigureAwait(false);
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
        finally
        {
            _pendingSlot.Release();
        }
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
        else if (string.Equals(command.Name, "logout", StringComparison.OrdinalIgnoreCase))
        {
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
        _pending = completion;

        try
        {
            var line = Encoding.UTF8.GetBytes(QueryCommandSerializer.ToWireLine(command) + '\n');
            await _shell.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            await _shell.FlushAsync(cancellationToken).ConfigureAwait(false);

            using var registration = cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));

            // A response that never arrives must surface as an error rather than hang the caller;
            // an MCP tool call has a person waiting at the other end of it.
            var response = await completion.Task
                .WaitAsync(_commandTimeout, cancellationToken)
                .ConfigureAwait(false);

            _lastActivity = DateTimeOffset.UtcNow;
            return response;
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

        while (!cancellationToken.IsCancellationRequested)
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
            DrainLines(carry);
        }
    }

    private void DrainLines(StringBuilder carry)
    {
        var text = carry.ToString();
        int newline;

        while ((newline = text.IndexOf('\n', StringComparison.Ordinal)) >= 0)
        {
            var line = text[..newline].Trim('\r', ' ');
            text = text[(newline + 1)..];

            if (line.Length > 0)
            {
                Dispatch(line);
            }
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
        if (pending is null)
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