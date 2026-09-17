using System.Diagnostics;

using ModelContextProtocol;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Mcp.Tools;

namespace TeamSpeak.Mcp.Tests.Tools;

/// <summary>The file actually opened must lie inside the local directory and have only one name.</summary>
public sealed class LocalFileGuardTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "tsmcp-guard-" + Guid.NewGuid().ToString("N"));

    public LocalFileGuardTests()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Outside);
    }

    private string Root => Path.Combine(_base, "root");

    private string Outside => Path.Combine(_base, "outside");

    public void Dispose() => Directory.Delete(_base, recursive: true);

    /// <summary>Creates a hard link with the operating system's own tool; no privilege is needed for one.</summary>
    private static void HardLink(string link, string target)
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/H", link, target])
            : new ProcessStartInfo("ln", [target, link]);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;

        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0 && File.Exists(link), $"Could not create a hard link: {process.StandardError.ReadToEnd()}");
    }

    [Fact]
    public async Task A_plain_file_inside_is_read_and_a_new_one_is_created_with_its_directories()
    {
        var existing = Path.Combine(Root, "a.txt");
        await File.WriteAllTextAsync(existing, "hello", TestContext.Current.CancellationToken);

        using (var read = LocalFileGuard.OpenRead(Root, existing))
        {
            Assert.Equal(5, read.Length);
        }

        var created = Path.Combine(Root, "new", "deeper", "b.bin");
        using (var write = LocalFileGuard.CreateNew(Root, created))
        {
            write.WriteByte(1);
        }

        using var append = LocalFileGuard.OpenAppend(Root, created);
        Assert.Equal(1, append.Position);
    }

    [Fact]
    public void A_hard_link_to_a_file_elsewhere_is_refused_for_reading_and_appending()
    {
        var secret = Path.Combine(Outside, "secret.txt");
        File.WriteAllText(secret, "outside");
        var link = Path.Combine(Root, "innocent.txt");
        HardLink(link, secret);

        Assert.Contains("hard link", Assert.Throws<McpException>(() => LocalFileGuard.OpenRead(Root, link)).Message, StringComparison.Ordinal);
        Assert.Throws<McpException>(() => LocalFileGuard.OpenAppend(Root, link));
        Assert.Equal("outside", File.ReadAllText(secret));
    }

    [Fact]
    public void A_partial_file_planted_as_a_link_to_a_file_elsewhere_is_not_appended_to()
    {
        var target = Path.Combine(Outside, "important.log");
        File.WriteAllText(target, "keep");
        var partial = Path.Combine(Root, "download.bin.partial");

        try
        {
            File.CreateSymbolicLink(partial, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Creating a symbolic link needs a privilege this machine does not grant: {ex.Message}");
        }

        var refused = Assert.Throws<McpException>(() => LocalFileGuard.OpenAppend(Root, partial));

        Assert.Contains("outside", refused.Message, StringComparison.Ordinal);
        Assert.Equal("keep", File.ReadAllText(target));
    }

    [Fact]
    public void A_directory_swapped_for_a_link_after_the_path_check_is_caught_when_the_file_opens()
    {
        // The path check has already passed for Root/sub/file.txt; now sub becomes a link to elsewhere.
        File.WriteAllText(Path.Combine(Outside, "file.txt"), "elsewhere");
        var sub = Path.Combine(Root, "sub");

        try
        {
            Directory.CreateSymbolicLink(sub, Outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Creating a symbolic link needs a privilege this machine does not grant: {ex.Message}");
        }

        Assert.Throws<McpException>(() => LocalFileGuard.OpenRead(Root, Path.Combine(sub, "file.txt")));
    }

    [Fact]
    public void The_new_defaults_keep_inline_content_near_the_warning_threshold_and_cap_local_saves() =>
        Assert.Equal((32 * 1024, 1024L * 1024 * 1024), (new FileTransferOptions().MaxInlineBytes, new FileTransferOptions().MaxLocalBytes));
}