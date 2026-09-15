using System.Net;
using System.Net.Sockets;

namespace TeamSpeak.Mcp.IntegrationTests;

/// <summary>
/// A local TCP relay to a live server that a test can break on purpose, at a moment of its choosing.
/// </summary>
/// <remarks>
/// <para>
/// A connection dropped by the network in the middle of a command cannot be staged against the server
/// itself: <c>quit</c> is answered, and killing the server takes the whole test server down. Routing the
/// session through this relay lets a test break it exactly between a command going out and its answer
/// coming back.
/// </para>
/// <para>
/// Two kinds of break are offered, because they reach the client in different ways. A cut resets both
/// sockets, as a server crash or a firewall reset does, so the client's SSH layer learns of it at once.
/// Silence keeps both sockets open and stops carrying bytes, as a pulled cable or a dead route does,
/// so the client learns of it only by waiting.
/// </para>
/// </remarks>
internal sealed class SeverableTcpProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Link> _links = [];
    private readonly Task _accepting;

    private int _armed;
    private int _accepted;

    private const int None = 0;
    private const int Cut = 1;
    private const int Silence = 2;

    public SeverableTcpProxy(string targetHost, int targetPort)
    {
        _targetHost = targetHost;
        _targetPort = targetPort;
        _listener.Start();
        _accepting = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Gets the local port clients connect to.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Gets how many client connections the relay has accepted so far.</summary>
    public int Accepted => Volatile.Read(ref _accepted);

    /// <summary>Gets how many connections were broken by <see cref="CutAfterNextClientWrite"/> or <see cref="SilenceAfterNextClientWrite"/>.</summary>
    public int Broken { get; private set; }

    /// <summary>
    /// Resets the connection right after the next bytes from the client have been passed to the server.
    /// </summary>
    public void CutAfterNextClientWrite() => Volatile.Write(ref _armed, Cut);

    /// <summary>
    /// Stops carrying bytes in either direction right after the next bytes from the client have been
    /// passed to the server, leaving both sockets open.
    /// </summary>
    public void SilenceAfterNextClientWrite() => Volatile.Write(ref _armed, Silence);

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        _listener.Stop();

        Link[] links;
        lock (_links)
        {
            links = [.. _links];
        }

        foreach (var link in links)
        {
            link.Close(reset: false);
        }

        try
        {
            await _accepting;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Stopping the listener ends the accept loop with one of these.
        }

        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
            Interlocked.Increment(ref _accepted);

            var server = new TcpClient();
            try
            {
                await server.ConnectAsync(_targetHost, _targetPort, _shutdown.Token);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                client.Dispose();
                server.Dispose();
                continue;
            }

            var link = new Link(client, server);
            lock (_links)
            {
                _links.Add(link);
            }

            _ = PumpAsync(link, fromClient: true);
            _ = PumpAsync(link, fromClient: false);
        }
    }

    private async Task PumpAsync(Link link, bool fromClient)
    {
        var source = fromClient ? link.Client.GetStream() : link.Server.GetStream();
        var target = fromClient ? link.Server.GetStream() : link.Client.GetStream();
        var buffer = new byte[16 * 1024];

        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, _shutdown.Token);
                if (read == 0)
                {
                    link.Close(reset: false);
                    return;
                }

                // A silenced link keeps reading so that neither side sees back pressure, but drops it all.
                if (link.Silenced)
                {
                    continue;
                }

                await target.WriteAsync(buffer.AsMemory(0, read), _shutdown.Token);

                if (!fromClient)
                {
                    continue;
                }

                switch (Interlocked.Exchange(ref _armed, None))
                {
                    case Cut:
                        Broken++;
                        link.Close(reset: true);
                        return;

                    case Silence:
                        Broken++;
                        link.Silenced = true;
                        break;

                    default:
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            link.Close(reset: false);
        }
    }

    private sealed class Link(TcpClient client, TcpClient server)
    {
        private int _closed;

        public TcpClient Client { get; } = client;

        public TcpClient Server { get; } = server;

        public volatile bool Silenced;

        public void Close(bool reset)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }

            if (reset)
            {
                // A zero linger makes Close send RST instead of FIN: an abrupt drop, not a goodbye.
                Client.Client.LingerState = new LingerOption(true, 0);
                Server.Client.LingerState = new LingerOption(true, 0);
            }

            Client.Dispose();
            Server.Dispose();
        }
    }
}