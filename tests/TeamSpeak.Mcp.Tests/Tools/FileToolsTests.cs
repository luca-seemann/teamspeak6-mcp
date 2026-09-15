using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

using ModelContextProtocol;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Mcp.Tools;
using TeamSpeak.Query.Client;
using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Mcp.Tests.Tools;

/// <summary>The file tools: safety, local path confinement, decoding, and transfers against a loopback port.</summary>
public sealed class FileToolsTests : IDisposable
{
    private const string Key = "829ebfa0809caf939de658f1fb71dafc";

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _localDirectory = Path.Combine(Path.GetTempPath(), "tsmcp-files-" + Guid.NewGuid().ToString("N"));

    public FileToolsTests()
    {
        _listener.Start();
        Directory.CreateDirectory(_localDirectory);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Port => ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);

    private FileTransferOptions Options(int maxInline = FileTransferOptions.DefaultMaxInlineBytes) =>
        new() { LocalDirectory = _localDirectory, MaxInlineBytes = maxInline };

    private static QueryProfile Loopback() => new() { Name = "test", Host = "127.0.0.1", Password = "secret" };

    private static Dictionary<string, string> Fields(params (string Key, string Value)[] fields) =>
        fields.ToDictionary(field => field.Key, field => field.Value);

    public void Dispose()
    {
        _listener.Dispose();
        Directory.Delete(_localDirectory, recursive: true);
    }

    /// <summary>Accepts one connection, reads the key and <paramref name="expect"/> bytes, then writes <paramref name="send"/>.</summary>
    private Task<byte[]> ServeOnceAsync(int expect = 0, byte[]? send = null) => Task.Run(async () =>
    {
        using var connection = await _listener.AcceptTcpClientAsync(Ct);
        var stream = connection.GetStream();
        var received = new byte[Key.Length + expect];
        await stream.ReadExactlyAsync(received, Ct);
        Assert.Equal(Key, Encoding.ASCII.GetString(received, 0, Key.Length));

        if (send is not null)
        {
            await stream.WriteAsync(send, Ct);
        }

        return received[Key.Length..];
    }, Ct);

    [Fact]
    public async Task A_read_only_profile_refuses_uploads_changes_and_deletions_before_sending_anything()
    {
        await using var harness = new ToolHarness(SafetyLevel.ReadOnly);
        var admin = new FileAdminTools(harness.Executor, Options());

        await Assert.ThrowsAsync<McpException>(() => admin.UploadAsync(1, "/a.txt", content: "x", cancellationToken: Ct));
        await Assert.ThrowsAsync<McpException>(() => admin.ManageAsync("createdir", 1, "/docs", cancellationToken: Ct));
        await Assert.ThrowsAsync<McpException>(() => admin.ManageAsync("stop", transferId: 3, cancellationToken: Ct));
        await Assert.ThrowsAsync<McpException>(() => admin.DeleteAsync(1, ["/a.txt"], cancellationToken: Ct));

        Assert.Empty(harness.Transport.SentCommands);
    }

    [Fact]
    public async Task Replacing_a_file_needs_destructive()
    {
        await using var harness = new ToolHarness(SafetyLevel.Write);

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            new FileAdminTools(harness.Executor, Options()).UploadAsync(1, "/a.txt", content: "x", overwrite: true, cancellationToken: Ct));

        Assert.Contains("Destructive", ex.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Transport.SentCommands);
    }

    [Fact]
    public async Task An_upload_announces_its_size_sends_the_bytes_and_checks_what_the_server_stored()
    {
        await using var harness = new ToolHarness(SafetyLevel.Write, Loopback());
        harness.Transport
            .Returns("ftinitupload", ToolHarness.Records(Fields(("clientftfid", "1"), ("serverftfid", "3"), ("ftkey", Key), ("port", Port), ("seekpos", "0"))))
            .Returns("ftgetfileinfo", ToolHarness.Records(Fields(("cid", "4"), ("name", "/docs/a.txt"), ("size", "6"))));
        var served = ServeOnceAsync(expect: 6);

        var result = await new FileAdminTools(harness.Executor, Options()).UploadAsync(4, "docs/a.txt", content: "hällo", virtualServerId: 2, cancellationToken: Ct);

        Assert.Equal("hällo"u8.ToArray(), await served);
        Assert.Equal(("/docs/a.txt", 6L), (result.Path, result.Bytes));

        var init = harness.Transport.SentCommands.First(command => command.Name == "ftinitupload");
        Assert.Equal(
            ("/docs/a.txt", "4", "", "6", "0", "0", 2),
            (init.Parameters!["name"], init.Parameters["cid"], init.Parameters["cpw"], init.Parameters["size"], init.Parameters["overwrite"], init.Parameters["resume"], init.VirtualServerId));
    }

    [Fact]
    public async Task An_upload_the_server_holds_only_part_of_is_reported_as_incomplete()
    {
        await using var harness = new ToolHarness(SafetyLevel.Write, Loopback());
        harness.Transport
            .Returns("ftinitupload", ToolHarness.Records(Fields(("serverftfid", "3"), ("ftkey", Key), ("port", Port))))
            .Returns("ftgetfileinfo", ToolHarness.Records(Fields(("size", "2"))));
        var served = ServeOnceAsync(expect: 5);

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            new FileAdminTools(harness.Executor, Options()).UploadAsync(1, "/a.bin", contentBase64: Convert.ToBase64String([1, 2, 3, 4, 5]), cancellationToken: Ct));

        await served;
        Assert.Contains("2 of 5 bytes", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Accepts connections one after another, reading each one's key and handing it to the next handler.</summary>
    private Task ServeInOrderAsync(params Func<NetworkStream, Task>[] handlers) => Task.Run(async () =>
    {
        foreach (var handle in handlers)
        {
            using var connection = await _listener.AcceptTcpClientAsync(Ct);
            var stream = connection.GetStream();
            await stream.ReadExactlyAsync(new byte[Key.Length], Ct);
            await handle(stream);
        }
    }, Ct);

    [Fact]
    public async Task Resuming_compares_the_stored_tail_and_then_sends_only_the_missing_bytes()
    {
        await using var harness = new ToolHarness(SafetyLevel.Destructive, Loopback());
        var sizes = new Queue<string>(["3", "5"]);
        harness.Transport
            .Returns("ftgetfileinfo", _ => ToolHarness.Records(Fields(("size", sizes.Dequeue()))))
            .Returns("ftinitdownload", ToolHarness.Records(Fields(("serverftfid", "2"), ("ftkey", Key), ("port", Port), ("size", "3"))))
            .Returns("ftinitupload", ToolHarness.Records(Fields(("serverftfid", "3"), ("ftkey", Key), ("port", Port), ("seekpos", "3"))));
        var received = new byte[2];
        var served = ServeInOrderAsync(
            stream => stream.WriteAsync("hel"u8.ToArray(), Ct).AsTask(),
            stream => stream.ReadExactlyAsync(received, Ct).AsTask());

        var result = await new FileAdminTools(harness.Executor, Options()).UploadAsync(1, "/a.txt", content: "hello", resume: true, cancellationToken: Ct);
        await served;

        Assert.Equal("lo"u8.ToArray(), received);
        Assert.Contains("from byte 3", result.Done, StringComparison.Ordinal);
        Assert.Equal(["ftgetfileinfo", "ftinitdownload", "ftinitupload", "ftgetfileinfo"], harness.Transport.SentCommands.Select(command => command.Name));
        Assert.Equal("0", harness.Transport.SentCommands[1].Parameters!["seekpos"]);

        var init = harness.Transport.SentCommands[2].Parameters!;
        Assert.Equal(("0", "1"), (init["overwrite"], init["resume"]));
    }

    [Fact]
    public async Task A_stored_file_that_ends_differently_is_not_resumed()
    {
        await using var harness = new ToolHarness(SafetyLevel.Destructive, Loopback());
        harness.Transport
            .Returns("ftgetfileinfo", ToolHarness.Records(Fields(("size", "3"))))
            .Returns("ftinitdownload", ToolHarness.Records(Fields(("ftkey", Key), ("port", Port), ("size", "3"))));
        var served = ServeInOrderAsync(stream => stream.WriteAsync("XYZ"u8.ToArray(), Ct).AsTask());

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            new FileAdminTools(harness.Executor, Options()).UploadAsync(1, "/a.txt", content: "hello", resume: true, cancellationToken: Ct));
        await served;

        Assert.Contains("differ", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.Transport.SentCommands, command => command.Name == "ftinitupload");
    }

    [Fact]
    public async Task A_stored_file_that_is_already_whole_is_reported_and_nothing_is_sent()
    {
        await using var harness = new ToolHarness(SafetyLevel.Destructive, Loopback());
        harness.Transport
            .Returns("ftgetfileinfo", ToolHarness.Records(Fields(("size", "5"))))
            .Returns("ftinitdownload", ToolHarness.Records(Fields(("ftkey", Key), ("port", Port), ("size", "5"))));
        var served = ServeInOrderAsync(stream => stream.WriteAsync("hello"u8.ToArray(), Ct).AsTask());

        var result = await new FileAdminTools(harness.Executor, Options()).UploadAsync(1, "/a.txt", content: "hello", resume: true, cancellationToken: Ct);
        await served;

        Assert.Contains("nothing was sent", result.Done, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.Transport.SentCommands, command => command.Name == "ftinitupload");
    }

    [Fact]
    public async Task Resuming_needs_destructive()
    {
        await using var harness = new ToolHarness(SafetyLevel.Write);

        await Assert.ThrowsAsync<McpException>(() =>
            new FileAdminTools(harness.Executor, Options()).UploadAsync(1, "/a.txt", content: "hello", resume: true, cancellationToken: Ct));

        Assert.Empty(harness.Transport.SentCommands);
    }

    [Fact]
    public async Task Resuming_and_overwriting_at_once_is_refused_before_sending_anything()
    {
        await using var harness = new ToolHarness(SafetyLevel.Destructive);

        await Assert.ThrowsAsync<McpException>(() =>
            new FileAdminTools(harness.Executor, Options()).UploadAsync(1, "/a.txt", content: "x", overwrite: true, resume: true, cancellationToken: Ct));

        Assert.Empty(harness.Transport.SentCommands);
    }

    [Fact]
    public async Task A_stored_file_larger_than_the_content_is_not_resumed()
    {
        await using var harness = new ToolHarness(SafetyLevel.Destructive, Loopback());
        harness.Transport.Returns("ftgetfileinfo", ToolHarness.Records(Fields(("size", "10"))));

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            new FileAdminTools(harness.Executor, Options()).UploadAsync(1, "/a.txt", content: "hello", resume: true, cancellationToken: Ct));

        Assert.Contains("overwrite=true", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["ftgetfileinfo"], harness.Transport.SentCommands.Select(command => command.Name));
    }

    [Fact]
    public async Task A_refusal_inside_the_upload_record_is_explained_and_nothing_is_transferred()
    {
        await using var harness = new ToolHarness(SafetyLevel.Write, Loopback());
        harness.Transport.Returns("ftinitupload", ToolHarness.Records(Fields(("clientftfid", "1"), ("status", "2050"), ("msg", "file already exists"), ("size", "5"))));

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            new FileAdminTools(harness.Executor, Options()).UploadAsync(1, "/a.txt", content: "hello", cancellationToken: Ct));

        Assert.Contains("2050", ex.Message, StringComparison.Ordinal);
        Assert.Contains("overwrite=true", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["ftinitupload"], harness.Transport.SentCommands.Select(command => command.Name));
    }

    [Fact]
    public async Task Upload_takes_exactly_one_source_and_rejects_bad_base64_and_oversized_content()
    {
        await using var harness = new ToolHarness(SafetyLevel.Write);
        var admin = new FileAdminTools(harness.Executor, Options(maxInline: 4));

        await Assert.ThrowsAsync<McpException>(() => admin.UploadAsync(1, "/a", cancellationToken: Ct));
        await Assert.ThrowsAsync<McpException>(() => admin.UploadAsync(1, "/a", content: "x", contentBase64: "eA==", cancellationToken: Ct));
        await Assert.ThrowsAsync<McpException>(() => admin.UploadAsync(1, "/a", contentBase64: "not base64!", cancellationToken: Ct));
        var tooLarge = await Assert.ThrowsAsync<McpException>(() => admin.UploadAsync(1, "/a", content: "hello", cancellationToken: Ct));
        await Assert.ThrowsAsync<McpException>(() => admin.UploadAsync(1, "/../a", content: "x", cancellationToken: Ct));

        Assert.Contains("localPath", tooLarge.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Transport.SentCommands);
    }

    [Fact]
    public async Task Local_paths_stay_inside_the_configured_directory_and_are_off_without_one()
    {
        await using var harness = new ToolHarness(SafetyLevel.Write);

        var outside = await Assert.ThrowsAsync<McpException>(() =>
            new FileAdminTools(harness.Executor, Options()).UploadAsync(1, "/a", localPath: Path.Combine("..", "secret.txt"), cancellationToken: Ct));
        var absolute = await Assert.ThrowsAsync<McpException>(() =>
            new FileTools(harness.Executor, Options()).DownloadAsync(1, "/a", localPath: Path.GetTempPath(), cancellationToken: Ct));
        var off = await Assert.ThrowsAsync<McpException>(() =>
            new FileAdminTools(harness.Executor, new FileTransferOptions()).UploadAsync(1, "/a", localPath: "a.txt", cancellationToken: Ct));

        Assert.Contains("outside", outside.Message, StringComparison.Ordinal);
        Assert.Contains("outside", absolute.Message, StringComparison.Ordinal);
        Assert.Contains("TeamSpeak:FileTransfer:LocalDirectory", off.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Transport.SentCommands);
    }

    [Fact]
    public async Task Icons_in_channel_0_are_listed_and_addressed_as_the_server_takes_them()
    {
        // Measured: the server lists /icon_123 under /icons, but refuses /icons/icon_123 with 1538.
        await using var harness = new ToolHarness(SafetyLevel.Destructive);
        harness.Transport
            .Returns("ftgetfilelist", ToolHarness.Records(Fields(("name", "icon_123"), ("type", "1"), ("size", "5"))))
            .Returns("ftgetfileinfo", ToolHarness.Records(Fields(("size", "5"))))
            .Returns("ftdeletefile", ToolHarness.Records());
        var files = new FileTools(harness.Executor, Options());
        var admin = new FileAdminTools(harness.Executor, Options());

        var listed = Assert.Single((await files.ListFilesAsync(0, "/icons", cancellationToken: Ct)).Entries);
        await files.FileInfoAsync(0, "/icons/icon_123", cancellationToken: Ct);
        await admin.DeleteAsync(0, ["/icons/icon_123"], cancellationToken: Ct);
        await files.FileInfoAsync(4, "/icons/icon_123", cancellationToken: Ct);

        Assert.Equal("/icon_123", listed.Path);
        Assert.Equal(
            ["/icon_123", "/icon_123", "/icons/icon_123"],
            harness.Transport.SentCommands.Where(command => command.Name != "ftgetfilelist").Select(command => command.Parameters!["name"]));
    }

    [Fact]
    public async Task A_download_returns_text_as_text_and_anything_else_as_base64()
    {
        await using var harness = new ToolHarness(SafetyLevel.ReadOnly, Loopback());
        var tools = new FileTools(harness.Executor, Options());

        harness.Transport.Returns("ftinitdownload", ToolHarness.Records(Fields(("serverftfid", "2"), ("ftkey", Key), ("port", Port), ("size", "6"))));
        var text = ServeOnceAsync(send: "hällo"u8.ToArray());
        var asText = await tools.DownloadAsync(1, "/a.txt", cancellationToken: Ct);
        await text;

        harness.Transport.Returns("ftinitdownload", ToolHarness.Records(Fields(("serverftfid", "3"), ("ftkey", Key), ("port", Port), ("size", "3"))));
        var binary = ServeOnceAsync(send: [0, 0xFF, 7]);
        var asBase64 = await tools.DownloadAsync(1, "/a.bin", cancellationToken: Ct);
        await binary;

        Assert.Equal(("text", "hällo"), (asText.Format, asText.Content));
        Assert.Equal(("base64", Convert.ToBase64String([0, 0xFF, 7])), (asBase64.Format, asBase64.Content));
        Assert.Equal("0", harness.Transport.SentCommands[0].Parameters!["seekpos"]);
    }

    [Fact]
    public async Task A_download_too_large_to_return_inline_asks_for_a_local_path()
    {
        await using var harness = new ToolHarness(SafetyLevel.ReadOnly, Loopback());
        harness.Transport.Returns("ftinitdownload", ToolHarness.Records(Fields(("ftkey", Key), ("port", Port), ("size", "10"))));

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            new FileTools(harness.Executor, Options(maxInline: 4)).DownloadAsync(1, "/big.bin", cancellationToken: Ct));

        Assert.Contains("localPath", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saving_a_download_locally_needs_write_while_returning_it_inline_does_not()
    {
        await using var harness = new ToolHarness(SafetyLevel.ReadOnly, Loopback());

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            new FileTools(harness.Executor, Options()).DownloadAsync(1, "/a.bin", localPath: "a.bin", cancellationToken: Ct));

        Assert.Contains("Write", ex.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Transport.SentCommands);
    }

    [Fact]
    public async Task A_download_saved_locally_never_replaces_an_existing_file()
    {
        await using var harness = new ToolHarness(SafetyLevel.Write, Loopback());
        harness.Transport.Returns("ftinitdownload", ToolHarness.Records(Fields(("ftkey", Key), ("port", Port), ("size", "3"))));
        var tools = new FileTools(harness.Executor, Options());
        var served = ServeOnceAsync(send: [1, 2, 3]);

        var saved = await tools.DownloadAsync(1, "/a.bin", localPath: Path.Combine("sub", "a.bin"), cancellationToken: Ct);
        await served;

        Assert.Equal(("file", Path.Combine(_localDirectory, "sub", "a.bin")), (saved.Format, saved.SavedTo));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(saved.SavedTo!, Ct));
        Assert.False(File.Exists(saved.SavedTo + ".partial"));

        await Assert.ThrowsAsync<McpException>(() => tools.DownloadAsync(1, "/a.bin", localPath: Path.Combine("sub", "a.bin"), cancellationToken: Ct));
        Assert.Single(harness.Transport.SentCommands);
    }

    [Fact]
    public async Task A_broken_download_keeps_what_arrived_and_resuming_fetches_only_the_rest()
    {
        await using var harness = new ToolHarness(SafetyLevel.Write, Loopback());
        harness.Transport.Returns("ftinitdownload", ToolHarness.Records(Fields(("ftkey", Key), ("port", Port), ("size", "5"))));
        var tools = new FileTools(harness.Executor, Options());
        var target = Path.Combine(_localDirectory, "a.bin");

        // The server closes after two of five bytes.
        var broken = ServeOnceAsync(send: [1, 2]);
        var ex = await Assert.ThrowsAsync<McpException>(() => tools.DownloadAsync(1, "/a.bin", localPath: "a.bin", cancellationToken: Ct));
        await broken;

        Assert.Contains("resume=true", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(target));
        Assert.Equal([1, 2], await File.ReadAllBytesAsync(target + ".partial", Ct));

        var rest = ServeOnceAsync(send: [3, 4, 5]);
        var resumed = await tools.DownloadAsync(1, "/a.bin", localPath: "a.bin", resume: true, cancellationToken: Ct);
        await rest;

        Assert.Equal(("2", 2L), (harness.Transport.SentCommands[1].Parameters!["seekpos"], resumed.ResumedFrom));
        Assert.Equal([1, 2, 3, 4, 5], await File.ReadAllBytesAsync(target, Ct));
        Assert.False(File.Exists(target + ".partial"));
    }

    [Fact]
    public async Task Resuming_a_download_needs_a_local_path_and_a_partial_file_that_fits()
    {
        await using var harness = new ToolHarness(SafetyLevel.Write, Loopback());
        harness.Transport.Returns("ftinitdownload", ToolHarness.Records(Fields(("ftkey", Key), ("port", Port), ("size", "3"))));
        var tools = new FileTools(harness.Executor, Options());

        await Assert.ThrowsAsync<McpException>(() => tools.DownloadAsync(1, "/a.bin", resume: true, cancellationToken: Ct));
        Assert.Empty(harness.Transport.SentCommands);

        await File.WriteAllBytesAsync(Path.Combine(_localDirectory, "b.bin.partial"), new byte[10], Ct);
        var tooLarge = await Assert.ThrowsAsync<McpException>(() => tools.DownloadAsync(1, "/b.bin", localPath: "b.bin", resume: true, cancellationToken: Ct));

        Assert.Contains("another file", tooLarge.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_file_is_explained()
    {
        await using var harness = new ToolHarness(SafetyLevel.ReadOnly);
        harness.Transport.Returns("ftinitdownload", ToolHarness.Records(Fields(("clientftfid", "14"), ("status", "2051"), ("msg", "file not found"), ("size", "0"))));

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            new FileTools(harness.Executor, Options()).DownloadAsync(1, "/nope.txt", cancellationToken: Ct));

        Assert.Contains("ts_file_list", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lists_files_and_directories_with_full_paths_and_unfinished_uploads()
    {
        await using var harness = new ToolHarness();
        harness.Transport.Returns("ftgetfilelist", ToolHarness.Records(
            Fields(("cid", "1"), ("path", "/docs"), ("name", "big.bin"), ("size", "3145728"), ("datetime", "1789427278119"), ("type", "1"), ("incompletesize", "4000000")),
            Fields(("name", "a dir"), ("size", "0"), ("datetime", "1789427291527"), ("type", "0"))));

        var list = await new FileTools(harness.Executor, Options()).ListFilesAsync(1, "docs/", cancellationToken: Ct);

        Assert.Equal("/docs", harness.Transport.SentCommands[0].Parameters!["path"]);
        Assert.Equal(
            [("/docs/big.bin", "file", 3145728L, (long?)4000000L), ("/docs/a dir", "directory", 0L, null)],
            list.Entries.Select(entry => (entry.Path, entry.Type, entry.Size, entry.IncompleteSize)));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1789427278119), list.Entries[0].Modified);
    }

    [Fact]
    public async Task An_empty_directory_is_an_empty_list()
    {
        await using var harness = new ToolHarness();
        harness.Transport.Returns("ftgetfilelist", ToolHarness.Error(QueryErrorCode.EmptyResultSet, "database empty result set"));

        Assert.Empty((await new FileTools(harness.Executor, Options()).ListFilesAsync(1, cancellationToken: Ct)).Entries);
    }

    [Fact]
    public async Task Lists_transfers_as_measured_on_a_stalled_upload()
    {
        await using var harness = new ToolHarness();
        harness.Transport.Returns("ftlist", ToolHarness.Records(Fields(
            ("clid", "4"), ("path", "files/virtualserver_1/channel_1"), ("name", "stalled.bin"), ("size", "1000000"), ("sizedone", "200000"),
            ("clientftfid", "26"), ("serverftfid", "6"), ("sender", "0"), ("status", "1"), ("current_speed", "174367.9219"),
            ("average_speed", "174367.9219"), ("runtime", "1"))));

        var transfer = Assert.Single((await new FileTools(harness.Executor, Options()).ListTransfersAsync(cancellationToken: Ct)).Transfers);

        Assert.Equal((6, "upload", "running", "stalled.bin", 200000L), (transfer.ServerTransferId, transfer.Direction, transfer.State, transfer.Name, transfer.BytesDone));
    }

    [Fact]
    public async Task Renaming_into_another_channel_sends_the_target_and_stopping_sends_the_transfer()
    {
        await using var harness = new ToolHarness(SafetyLevel.Destructive);
        harness.Transport.Returns("ftrenamefile", ToolHarness.Records()).Returns("ftstop", ToolHarness.Records());
        var admin = new FileAdminTools(harness.Executor, Options());

        var moved = await admin.ManageAsync("rename", 1, "/a.txt", "/b.txt", targetChannelId: 3, cancellationToken: Ct);
        await admin.ManageAsync("rename", 1, "/a.txt", "/b.txt", targetChannelId: 1, cancellationToken: Ct);
        await admin.ManageAsync("stop", transferId: 6, deletePartial: true, cancellationToken: Ct);

        var across = harness.Transport.SentCommands[0].Parameters!;
        Assert.Equal(["cid", "cpw", "tcid", "tcpw", "oldname", "newname"], across.Keys);
        Assert.Equal("3", across["tcid"]);
        Assert.Contains("channel 3", moved.Done, StringComparison.Ordinal);
        Assert.DoesNotContain("tcid", harness.Transport.SentCommands[1].Parameters!.Keys);
        Assert.Equal(("6", "1"), (harness.Transport.SentCommands[2].Parameters!["serverftfid"], harness.Transport.SentCommands[2].Parameters!["delete"]));
    }

    [Fact]
    public async Task Removing_a_partial_file_when_stopping_needs_destructive()
    {
        await using var harness = new ToolHarness(SafetyLevel.Write);
        harness.Transport.Returns("ftstop", ToolHarness.Records());
        var admin = new FileAdminTools(harness.Executor, Options());

        await Assert.ThrowsAsync<McpException>(() => admin.ManageAsync("stop", transferId: 6, deletePartial: true, cancellationToken: Ct));
        Assert.Empty(harness.Transport.SentCommands);

        await admin.ManageAsync("stop", transferId: 6, cancellationToken: Ct);
        Assert.Equal("0", Assert.Single(harness.Transport.SentCommands).Parameters!["delete"]);
    }

    [Fact]
    public async Task Server_paths_keep_spaces_and_backslashes_as_part_of_names()
    {
        await using var harness = new ToolHarness();
        harness.Transport.Returns("ftgetfileinfo", ToolHarness.Records(Fields(("size", "1"))));

        await new FileTools(harness.Executor, Options()).FileInfoAsync(1, "/reports /a\\b.txt ", cancellationToken: Ct);

        Assert.Equal("/reports /a\\b.txt ", harness.Transport.SentCommands[0].Parameters!["name"]);
    }

    [Fact]
    public async Task A_partial_file_this_download_did_not_make_is_left_alone()
    {
        await using var harness = new ToolHarness(SafetyLevel.Write, Loopback());
        var partial = Path.Combine(_localDirectory, "mine.bin.partial");
        await File.WriteAllBytesAsync(partial, [9, 9, 9], Ct);

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            new FileTools(harness.Executor, Options()).DownloadAsync(1, "/mine.bin", localPath: "mine.bin", cancellationToken: Ct));

        Assert.Contains("resume=true", ex.Message, StringComparison.Ordinal);
        Assert.Equal([9, 9, 9], await File.ReadAllBytesAsync(partial, Ct));
        Assert.Empty(harness.Transport.SentCommands);
    }

    [Fact]
    public async Task The_local_directory_must_be_absolute_existing_and_not_a_root_and_paths_must_be_plain()
    {
        await using var harness = new ToolHarness(SafetyLevel.Write);
        var root = Path.GetPathRoot(_localDirectory)!;

        foreach (var (directory, expected) in new[]
                 {
                     ("relative/files", "absolute"),
                     (root, "root"),
                     (Path.Combine(_localDirectory, "missing"), "does not exist"),
                 })
        {
            var ex = await Assert.ThrowsAsync<McpException>(() =>
                new FileAdminTools(harness.Executor, new FileTransferOptions { LocalDirectory = directory }).UploadAsync(1, "/a", localPath: "a.txt", cancellationToken: Ct));
            Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        }

        await Assert.ThrowsAsync<McpException>(() =>
            new FileAdminTools(harness.Executor, Options()).UploadAsync(1, "/a", localPath: "a\0.txt", cancellationToken: Ct));
        Assert.Empty(harness.Transport.SentCommands);
    }

    [Fact]
    public async Task A_link_inside_the_local_directory_is_not_followed()
    {
        var outside = Path.Combine(Path.GetTempPath(), "tsmcp-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "secret", Ct);

        try
        {
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(_localDirectory, "escape"), outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Assert.Skip($"Creating a symbolic link needs a privilege this machine does not grant: {ex.Message}");
            }

            await using var harness = new ToolHarness(SafetyLevel.Write);
            var refused = await Assert.ThrowsAsync<McpException>(() =>
                new FileAdminTools(harness.Executor, Options()).UploadAsync(1, "/a", localPath: Path.Combine("escape", "secret.txt"), cancellationToken: Ct));

            Assert.Contains("link", refused.Message, StringComparison.Ordinal);
            Assert.Empty(harness.Transport.SentCommands);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task Deleting_stops_at_the_first_failure_and_names_what_was_already_deleted()
    {
        await using var harness = new ToolHarness(SafetyLevel.Destructive);
        harness.Transport.Returns("ftdeletefile", command => command.Parameters!["name"] == "/b"
            ? ToolHarness.Error(QueryErrorCode.InvalidFilePath, "invalid file path")
            : ToolHarness.Records());

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            new FileAdminTools(harness.Executor, Options()).DeleteAsync(1, ["/a", "b", "/c"], cancellationToken: Ct));

        Assert.Contains("Deleted before that: /a.", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, harness.Transport.SentCommands.Count);
    }

    [Fact]
    public void Explains_that_the_web_query_refuses_file_commands_whatever_the_key()
    {
        var file = QueryExecutor.DescribeRefusal("web", "ftgetfilelist", new QueryError(QueryErrorCode.OutOfScope, "out of scope"));
        var other = QueryExecutor.DescribeRefusal("web", "servernotifyregister", new QueryError(QueryErrorCode.OutOfScope, "out of scope"));

        Assert.Contains("SSH password", file, StringComparison.Ordinal);
        Assert.DoesNotContain("scope=manage", file, StringComparison.Ordinal);
        Assert.Contains("scope=manage", other, StringComparison.Ordinal);
    }
}