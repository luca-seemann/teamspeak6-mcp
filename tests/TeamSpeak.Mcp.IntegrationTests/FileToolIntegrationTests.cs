using System.Globalization;
using System.Net.Sockets;
using System.Text;

using ModelContextProtocol;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Mcp.Safety;
using TeamSpeak.Mcp.Tools;
using TeamSpeak.Query.Client;
using TeamSpeak.Query.FileTransfer;
using TeamSpeak.Query.Protocol;
using TeamSpeak.Query.Transport;

namespace TeamSpeak.Mcp.IntegrationTests;

/// <summary>
/// Moves real bytes through the file tools against a live TeamSpeak 6 server, over the SSH session
/// and the file transfer port.
/// </summary>
/// <remarks>
/// Everything is created under a unique "tsmcp-live" name and deleted in a finally block. The server's
/// file transfer port (30033) has to be reachable from the machine running the tests.
/// </remarks>
[Collection(LiveServerDefinition.Name)]
public sealed class FileToolIntegrationTests(LiveServerFixture server) : IDisposable
{
    private readonly string _localDirectory = Path.Combine(Path.GetTempPath(), "tsmcp-live-files-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unique() => "tsmcp-live-" + Guid.NewGuid().ToString("N")[..8];

    public void Dispose()
    {
        if (Directory.Exists(_localDirectory))
        {
            Directory.Delete(_localDirectory, recursive: true);
        }
    }

    private QueryConnectionManager Connections() =>
        new(
            new ProfileRegistry([LiveServerFixture.Profile()]),
            (_, _, _) => Task.FromResult<IQueryTransport>(new ToolIntegrationTests.BorrowedTransport(server.Ssh)));

    private FileTransferOptions Options()
    {
        Directory.CreateDirectory(_localDirectory);
        return new FileTransferOptions { LocalDirectory = _localDirectory };
    }

    private static async Task<(int First, int Second)> TwoChannelsAsync(QueryExecutor executor)
    {
        var channels = (await new ChannelTools(executor).ListChannelsAsync(virtualServerId: 1, cancellationToken: Ct)).Channels;
        return (channels[0].Id, channels[1].Id);
    }

    /// <summary>Removes a leftover without failing the test, for finally blocks.</summary>
    private async Task DeleteQuietlyAsync(int channelId, string name)
    {
        await server.Ssh.SendAsync(
            new QueryCommand(
                "ftdeletefile",
                new Dictionary<string, string> { ["cid"] = channelId.ToString(CultureInfo.InvariantCulture), ["cpw"] = "", ["name"] = name },
                VirtualServerId: 1),
            CancellationToken.None);
    }

    [RequiresTeamSpeakServerFact]
    public async Task Files_can_be_uploaded_listed_downloaded_replaced_moved_and_deleted()
    {
        await using var connections = Connections();
        var executor = new QueryExecutor(connections, new SafetyPolicy(SafetyLevel.Destructive));
        var files = new FileTools(executor, Options());
        var admin = new FileAdminTools(executor, Options());
        var (channel, otherChannel) = await TwoChannelsAsync(executor);

        var directory = "/" + Unique();
        var moved = "/" + Unique() + ".bin";

        try
        {
            await admin.ManageAsync("createdir", channel, directory, virtualServerId: 1, cancellationToken: Ct);

            const string text = "Hallo TeamSpeak — äöü ✓\n";
            var uploaded = await admin.UploadAsync(channel, $"{directory}/hello.txt", content: text, virtualServerId: 1, cancellationToken: Ct);
            Assert.Equal(Encoding.UTF8.GetByteCount(text), uploaded.Bytes);

            var entry = Assert.Single((await files.ListFilesAsync(channel, directory, virtualServerId: 1, cancellationToken: Ct)).Entries);
            Assert.Equal(($"{directory}/hello.txt", "file", uploaded.Bytes, (long?)null), (entry.Path, entry.Type, entry.Size, entry.IncompleteSize));
            Assert.InRange(entry.Modified!.Value, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

            // ftgetfileinfo writes nanoseconds where ftgetfilelist writes milliseconds; both must decode to the same moment.
            var info = await files.FileInfoAsync(channel, $"{directory}/hello.txt", virtualServerId: 1, cancellationToken: Ct);
            Assert.Equal(uploaded.Bytes, info.Size);
            Assert.InRange((info.Modified!.Value - entry.Modified.Value).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(1));

            var downloaded = await files.DownloadAsync(channel, $"{directory}/hello.txt", virtualServerId: 1, cancellationToken: Ct);
            Assert.Equal(("text", text), (downloaded.Format, downloaded.Content));

            // Binary, several chunks long, through local files in both directions.
            var binary = new byte[300_000];
            new Random(8).NextBytes(binary);
            await File.WriteAllBytesAsync(Path.Combine(_localDirectory, "up.bin"), binary, Ct);

            await admin.UploadAsync(channel, $"{directory}/data.bin", localPath: "up.bin", virtualServerId: 1, cancellationToken: Ct);
            var saved = await files.DownloadAsync(channel, $"{directory}/data.bin", localPath: Path.Combine("down", "data.bin"), virtualServerId: 1, cancellationToken: Ct);
            Assert.Equal(binary, await File.ReadAllBytesAsync(saved.SavedTo!, Ct));

            var refused = await Assert.ThrowsAsync<McpException>(() =>
                admin.UploadAsync(channel, $"{directory}/hello.txt", content: "again", virtualServerId: 1, cancellationToken: Ct));
            Assert.Contains("2050", refused.Message, StringComparison.Ordinal);

            await admin.UploadAsync(channel, $"{directory}/hello.txt", content: "replaced", overwrite: true, virtualServerId: 1, cancellationToken: Ct);
            Assert.Equal("replaced", (await files.DownloadAsync(channel, $"{directory}/hello.txt", virtualServerId: 1, cancellationToken: Ct)).Content);

            var missing = await Assert.ThrowsAsync<McpException>(() =>
                files.DownloadAsync(channel, $"{directory}/nothing.txt", virtualServerId: 1, cancellationToken: Ct));
            Assert.Contains("2051", missing.Message, StringComparison.Ordinal);

            var noDirectory = await Assert.ThrowsAsync<McpException>(() =>
                admin.UploadAsync(channel, $"{directory}/no such dir/x.txt", content: "x", virtualServerId: 1, cancellationToken: Ct));
            Assert.Contains("2054", noDirectory.Message, StringComparison.Ordinal);

            await admin.ManageAsync("rename", channel, $"{directory}/data.bin", moved, targetChannelId: otherChannel, virtualServerId: 1, cancellationToken: Ct);
            Assert.Contains((await files.ListFilesAsync(otherChannel, virtualServerId: 1, cancellationToken: Ct)).Entries, file => file.Path == moved && file.Size == binary.Length);

            await admin.DeleteAsync(otherChannel, [moved], virtualServerId: 1, cancellationToken: Ct);

            // A directory is deleted with what it holds.
            await admin.DeleteAsync(channel, [directory], virtualServerId: 1, cancellationToken: Ct);
            Assert.DoesNotContain((await files.ListFilesAsync(channel, virtualServerId: 1, cancellationToken: Ct)).Entries, file => file.Path == directory);
            Assert.DoesNotContain((await files.ListFilesAsync(otherChannel, virtualServerId: 1, cancellationToken: Ct)).Entries, file => file.Path == moved);
        }
        finally
        {
            await DeleteQuietlyAsync(channel, directory);
            await DeleteQuietlyAsync(otherChannel, moved);
        }
    }

    [RequiresTeamSpeakServerFact]
    public async Task An_unfinished_upload_is_listed_and_stopping_it_removes_the_partial_file()
    {
        await using var connections = Connections();
        var executor = new QueryExecutor(connections, new SafetyPolicy(SafetyLevel.Write));
        var files = new FileTools(executor, Options());
        var (channel, _) = await TwoChannelsAsync(executor);
        var name = "/" + Unique() + ".bin";

        var init = await server.Ssh.SendAsync(
            new QueryCommand(
                "ftinitupload",
                new Dictionary<string, string>
                {
                    ["clientftfid"] = "900", ["name"] = name, ["cid"] = channel.ToString(CultureInfo.InvariantCulture), ["cpw"] = "",
                    ["size"] = "1000000", ["overwrite"] = "1", ["resume"] = "0",
                },
                VirtualServerId: 1),
            Ct);
        var ticket = FileTransferTicket.FromRecord(init.Records[0]);

        using var stalled = new TcpClient();
        try
        {
            // Send a fifth of the file and then nothing, holding the connection open.
            await stalled.ConnectAsync(LiveServer.Host, ticket.Port, Ct);
            var stream = stalled.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(ticket.Key), Ct);
            await stream.WriteAsync(new byte[200_000], Ct);
            await stream.FlushAsync(Ct);
            await Task.Delay(TimeSpan.FromSeconds(1), Ct);

            var transfer = Assert.Single(
                (await files.ListTransfersAsync(virtualServerId: 1, cancellationToken: Ct)).Transfers,
                candidate => candidate.ServerTransferId == ticket.ServerTransferId);
            Assert.Equal(("upload", "running", 1000000L), (transfer.Direction, transfer.State, transfer.Size));
            Assert.True(transfer.BytesDone > 0);

            var partial = Assert.Single((await files.ListFilesAsync(channel, virtualServerId: 1, cancellationToken: Ct)).Entries, entry => entry.Path == name);
            Assert.Equal(1000000L, partial.IncompleteSize);

            var stop = await new FileAdminTools(executor, Options()).ManageAsync(
                "stop", transferId: ticket.ServerTransferId, deletePartial: true, virtualServerId: 1, cancellationToken: Ct);
            Assert.Contains("removed", stop.Done, StringComparison.Ordinal);

            await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
            Assert.DoesNotContain((await files.ListFilesAsync(channel, virtualServerId: 1, cancellationToken: Ct)).Entries, entry => entry.Path == name);
        }
        finally
        {
            await DeleteQuietlyAsync(channel, name);
        }
    }

    [RequiresTeamSpeakServerFact]
    public async Task An_upload_that_broke_off_is_resumed_to_an_identical_file()
    {
        await using var connections = Connections();
        var executor = new QueryExecutor(connections, new SafetyPolicy(SafetyLevel.Write));
        var options = Options();
        var (channel, _) = await TwoChannelsAsync(executor);
        var name = "/" + Unique() + ".bin";

        var content = new byte[300_000];
        new Random(21).NextBytes(content);
        await File.WriteAllBytesAsync(Path.Combine(_localDirectory, "resume.bin"), content, Ct);

        try
        {
            // Break an upload off after a third of the file, as a dropped connection would.
            var init = await server.Ssh.SendAsync(
                new QueryCommand(
                    "ftinitupload",
                    new Dictionary<string, string>
                    {
                        ["clientftfid"] = "901", ["name"] = name, ["cid"] = channel.ToString(CultureInfo.InvariantCulture), ["cpw"] = "",
                        ["size"] = content.Length.ToString(CultureInfo.InvariantCulture), ["overwrite"] = "1", ["resume"] = "0",
                    },
                    VirtualServerId: 1),
                Ct);
            var ticket = FileTransferTicket.FromRecord(init.Records[0]);

            using (var broken = new TcpClient())
            {
                await broken.ConnectAsync(LiveServer.Host, ticket.Port, Ct);
                var stream = broken.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes(ticket.Key), Ct);
                await stream.WriteAsync(content.AsMemory(0, 100_000), Ct);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
            var files = new FileTools(executor, options);
            var partial = Assert.Single((await files.ListFilesAsync(channel, virtualServerId: 1, cancellationToken: Ct)).Entries, entry => entry.Path == name);
            Assert.True(partial.Size < content.Length);

            var resumed = await new FileAdminTools(executor, options).UploadAsync(
                channel, name, localPath: "resume.bin", resume: true, virtualServerId: 1, cancellationToken: Ct);
            Assert.Contains($"from byte {partial.Size}", resumed.Done, StringComparison.Ordinal);

            var saved = await files.DownloadAsync(channel, name, localPath: "resumed.bin", virtualServerId: 1, cancellationToken: Ct);
            Assert.Equal(content, await File.ReadAllBytesAsync(saved.SavedTo!, Ct));
        }
        finally
        {
            await DeleteQuietlyAsync(channel, name);
        }
    }

    [RequiresWebQueryFact]
    public async Task The_web_query_refuses_file_commands_with_an_explanation()
    {
        var profile = LiveServerFixture.Profile();
        profile.Transport = PreferredTransport.WebQuery;
        await using var connections = new QueryConnectionManager(new ProfileRegistry([profile]));
        var executor = new QueryExecutor(connections, new SafetyPolicy(SafetyLevel.ReadOnly));

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            new FileTools(executor, new FileTransferOptions()).ListFilesAsync(1, virtualServerId: 1, cancellationToken: Ct));

        Assert.Contains("5120", ex.Message, StringComparison.Ordinal);
        Assert.Contains("SSH password", ex.Message, StringComparison.Ordinal);
    }
}
