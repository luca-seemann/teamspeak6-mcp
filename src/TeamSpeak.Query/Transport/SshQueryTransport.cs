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
/// repeatedly is what trips the server's flood protection. A <see cref="FloodGuard"/> serialises
/// callers and keeps a gap between commands.
/// </para>
/// <para>
/// Two details of the real server drive this implementation and appear nowhere in its
/// documentation. The server refuses pseudo-terminal requests, so the channel has to be opened
/// without one. And it emits no prompt at all — the greeting line is <c>TS3</c> — so responses can
/// only be framed on the trailing <c>error id=</c> line.
/// </para>
/// </remarks>
public sealed class SshQueryTransport : IQueryTransport
{
    private readonly SshClient _client;
    private readonly ShellStream _shell;
    private readonly FloodGuard _guard;
    private readonly TimeSpan _commandTimeout;
    private readonly Channel<QueryEvent> _events = Channel.CreateBounded<QueryEvent>(
        new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly SemaphoreSlim _pendingSlot = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _readerLoop;

    private TaskCompletionSource<QueryResponse>? _pending;
    private StringBuilder _pendingText = new();

    private SshQueryTransport(SshClient client, ShellStream shell, FloodGuard guard, TimeSpan commandTimeout)
    {
        _client = client;
        _shell = shell;
        _guard = guard;
        _commandTimeout = commandTimeout;
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

        var connectionInfo = new ConnectionInfo(
            profile.Host,
            profile.SshPort,
            profile.Username,
            new PasswordAuthenticationMethod(profile.Username, profile.Password!))
        {
            // Cheap traffic keeps the TCP session alive; it does not count as query activity.
            Timeout = TimeSpan.FromSeconds(30),
        };

        var client = new SshClient(connectionInfo);
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // The server rejects a PTY request, so the terminal-less channel is mandatory.
            var shell = client.CreateShellStreamNoTerminal(bufferSize: 256 * 1024);
            var transport = new SshQueryTransport(
                client, shell, new FloodGuard(profile.CommandInterval), profile.CommandTimeout);

            await ConsumeGreetingAsync(cancellationToken).ConfigureAwait(false);
            return transport;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<QueryResponse> SendAsync(
        QueryCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var response = await SendOnceAsync(command, cancellationToken).ConfigureAwait(false);

        // The server states how long to wait; honouring it is what keeps a rejection from
        // escalating into an IP-level block.
        if (response.Error.IsFlooding)
        {
            _guard.PenaliseFor(response.Error.RetryAfter);
            response = await SendOnceAsync(command, cancellationToken).ConfigureAwait(false);
        }

        return response;
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

        // ShellStream.ReadAsync does not honour a cancellation token, so cancelling alone leaves
        // the reader blocked forever. Disposing the stream is what actually unblocks it.
        _shell.Dispose();

        try
        {
            await _readerLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // The reader is being torn down; a stuck one must not block disposal.
        }

        _events.Writer.TryComplete();
        _client.Dispose();
        _shutdown.Dispose();
        _pendingSlot.Dispose();
        _guard.Dispose();
    }

    private async Task<QueryResponse> SendOnceAsync(QueryCommand command, CancellationToken cancellationToken)
    {
        using var lease = await _guard.AcquireAsync(cancellationToken).ConfigureAwait(false);

        await _pendingSlot.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var completion = new TaskCompletionSource<QueryResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            _pendingText = new StringBuilder();
            _pending = completion;

            var line = Encoding.UTF8.GetBytes(QueryCommandSerializer.ToWireLine(command) + '\n');
            await _shell.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            await _shell.FlushAsync(cancellationToken).ConfigureAwait(false);

            using var registration = cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));

            // A response that never arrives must surface as an error rather than hang the caller;
            // an MCP tool call has a person waiting at the other end of it.
            return await completion.Task.WaitAsync(_commandTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending = null;
            _pendingSlot.Release();
        }
    }

    private static async Task ConsumeGreetingAsync(CancellationToken cancellationToken)
    {
        // The greeting is two lines and carries no status line, so it cannot be framed like a
        // response; give it a moment and discard whatever arrives.
        await Task.Delay(TimeSpan.FromMilliseconds(600), cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var carry = new StringBuilder();

        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await _shell.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }

            if (read <= 0)
            {
                FailPending(new QueryProtocolException("The SSH session closed while awaiting a response."));
                break;
            }

            carry.Append(Encoding.UTF8.GetString(buffer, 0, read));
            DrainLines(carry);
        }

        _events.Writer.TryComplete();
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

    private void FailPending(Exception error) => _pending?.TrySetException(error);
}