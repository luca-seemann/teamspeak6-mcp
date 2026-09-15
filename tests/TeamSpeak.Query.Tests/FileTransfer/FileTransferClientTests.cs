using System.Net;
using System.Net.Sockets;
using System.Text;

using TeamSpeak.Query.FileTransfer;

namespace TeamSpeak.Query.Tests.FileTransfer;

/// <summary>The byte transfer against a loopback listener that behaves as the live server was measured to.</summary>
public sealed class FileTransferClientTests : IDisposable
{
    private const string Key = "829ebfa0809caf939de658f1fb71dafc";

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public FileTransferClientTests() => _listener.Start();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private FileTransferTicket Ticket(long size = 0, params string[] addresses) =>
        new(1, 7, Key, Port, size, 0, addresses);

    public void Dispose() => _listener.Dispose();

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, Ct);
        return buffer;
    }

    [Fact]
    public async Task An_upload_sends_the_key_then_the_bytes_and_waits_for_the_server_to_close()
    {
        var payload = new byte[200_000];
        Random.Shared.NextBytes(payload);

        var server = Task.Run(async () =>
        {
            using var connection = await _listener.AcceptTcpClientAsync(Ct);
            var stream = connection.GetStream();
            var key = Encoding.ASCII.GetString(await ReadExactlyAsync(stream, Key.Length));
            var body = await ReadExactlyAsync(stream, payload.Length);
            return (key, body);
        }, Ct);

        using var source = new MemoryStream(payload);
        await FileTransferClient.UploadAsync(Ticket(), "127.0.0.1", source, payload.Length, Ct);

        var (receivedKey, receivedBody) = await server;
        Assert.Equal(Key, receivedKey);
        Assert.Equal(payload, receivedBody);
    }

    [Fact]
    public async Task An_empty_upload_still_presents_its_key()
    {
        // Measured: a zero-byte upload only exists on the server once its key was presented.
        var server = Task.Run(async () =>
        {
            using var connection = await _listener.AcceptTcpClientAsync(Ct);
            return Encoding.ASCII.GetString(await ReadExactlyAsync(connection.GetStream(), Key.Length));
        }, Ct);

        await FileTransferClient.UploadAsync(Ticket(), "127.0.0.1", new MemoryStream(), 0, Ct);

        Assert.Equal(Key, await server);
    }

    [Fact]
    public async Task An_upload_fails_when_the_source_holds_fewer_bytes_than_announced()
    {
        var server = Task.Run(async () =>
        {
            using var connection = await _listener.AcceptTcpClientAsync(Ct);
            await connection.GetStream().CopyToAsync(Stream.Null, Ct);
        }, Ct);

        var ex = await Assert.ThrowsAsync<IOException>(() =>
            FileTransferClient.UploadAsync(Ticket(), "127.0.0.1", new MemoryStream(new byte[10]), 20, Ct));

        Assert.Contains("10 of 20", ex.Message, StringComparison.Ordinal);
        await server;
    }

    [Fact]
    public async Task A_download_reads_exactly_the_expected_bytes()
    {
        var payload = Encoding.UTF8.GetBytes(new string('x', 100_000));

        var server = Task.Run(async () =>
        {
            using var connection = await _listener.AcceptTcpClientAsync(Ct);
            var stream = connection.GetStream();
            var key = Encoding.ASCII.GetString(await ReadExactlyAsync(stream, Key.Length));
            await stream.WriteAsync(payload, Ct);
            return key;
        }, Ct);

        using var destination = new MemoryStream();
        await FileTransferClient.DownloadAsync(Ticket(payload.Length), "127.0.0.1", destination, payload.Length, Ct);

        Assert.Equal(Key, await server);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact]
    public async Task A_download_the_server_ends_early_is_an_error_not_a_short_file()
    {
        var server = Task.Run(async () =>
        {
            using var connection = await _listener.AcceptTcpClientAsync(Ct);
            var stream = connection.GetStream();
            await ReadExactlyAsync(stream, Key.Length);
            await stream.WriteAsync(new byte[300], Ct);
        }, Ct);

        var ex = await Assert.ThrowsAsync<IOException>(() =>
            FileTransferClient.DownloadAsync(Ticket(1000), "127.0.0.1", new MemoryStream(), 1000, Ct));

        Assert.Contains("300 of 1000", ex.Message, StringComparison.Ordinal);
        await server;
    }

    [Fact]
    public async Task A_stalled_download_times_out()
    {
        var release = new TaskCompletionSource();
        var server = Task.Run(async () =>
        {
            using var connection = await _listener.AcceptTcpClientAsync(Ct);
            await ReadExactlyAsync(connection.GetStream(), Key.Length);
            await release.Task;
        }, Ct);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            FileTransferClient.DownloadAsync(Ticket(10), "127.0.0.1", new MemoryStream(), 10, Ct, TimeSpan.FromMilliseconds(300)));

        release.SetResult();
        await server;
    }

    [Fact]
    public async Task Falls_back_to_an_address_the_server_names_when_the_configured_host_is_unreachable()
    {
        var server = Task.Run(async () =>
        {
            using var connection = await _listener.AcceptTcpClientAsync(Ct);
            var stream = connection.GetStream();
            await ReadExactlyAsync(stream, Key.Length);
            await stream.WriteAsync(new byte[] { 1, 2, 3 }, Ct);
        }, Ct);

        // Nothing listens on this port at 127.0.0.2, so the connection is refused at once.
        using var destination = new MemoryStream();
        await FileTransferClient.DownloadAsync(Ticket(3, "127.0.0.1"), "127.0.0.2", destination, 3, Ct);

        Assert.Equal([1, 2, 3], destination.ToArray());
        await server;
    }

    [Fact]
    public async Task Names_every_address_it_tried_when_none_answers()
    {
        var ticket = Ticket(3);
        _listener.Stop();

        var ex = await Assert.ThrowsAsync<IOException>(() =>
            FileTransferClient.DownloadAsync(ticket, "127.0.0.1", new MemoryStream(), 3, Ct));

        Assert.Contains($"127.0.0.1:{ticket.Port}", ex.Message, StringComparison.Ordinal);
    }
}