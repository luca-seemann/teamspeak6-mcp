using TeamSpeak.Query.Transport;

namespace TeamSpeak.Query.Tests.Transport;

/// <summary>Which SSH host keys are trusted: remembered on first use, or pinned.</summary>
public sealed class HostKeysTests : IDisposable
{
    private const string KeyA = "SHA256:ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og";
    private const string KeyB = "SHA256:zzz8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tsmcp-hostkeys-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "nested", "known_hosts");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void The_first_key_is_remembered_with_its_directory_and_trusted_again()
    {
        var file = new KnownHostsFile(FilePath);

        Assert.True(file.Verify("TS.example.com", 10022, "ssh-ed25519", KeyA).Trusted);
        Assert.True(file.Verify("ts.example.com", 10022, "ssh-ed25519", KeyA).Trusted);

        Assert.Equal(["ts.example.com:10022 ssh-ed25519 " + KeyA], File.ReadAllLines(FilePath));
        Assert.Equal(KeyA, file.Find("ts.example.com", 10022));
    }

    [Fact]
    public void A_different_key_later_is_refused_and_the_remembered_one_stays()
    {
        var file = new KnownHostsFile(FilePath);
        file.Verify("ts.example.com", 10022, "ssh-ed25519", KeyA);

        var verdict = file.Verify("ts.example.com", 10022, "ssh-ed25519", KeyB);

        Assert.False(verdict.Trusted);
        Assert.Contains(KeyA, verdict.Reason, StringComparison.Ordinal);
        Assert.Contains(KeyB, verdict.Reason, StringComparison.Ordinal);
        Assert.Contains(FilePath, verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("HostKeyFingerprint", verdict.Reason, StringComparison.Ordinal);
        Assert.Single(File.ReadAllLines(FilePath));
    }

    [Fact]
    public void Each_port_is_its_own_server()
    {
        var file = new KnownHostsFile(FilePath);
        file.Verify("ts.example.com", 10022, "ssh-ed25519", KeyA);

        Assert.True(file.Verify("ts.example.com", 10023, "ssh-ed25519", KeyB).Trusted);
        Assert.Null(file.Find("other.example.com", 10022));
    }

    [Fact]
    public void Comments_blank_lines_and_a_missing_final_newline_are_tolerated()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "# pinned by hand\n\nts.example.com:10022 ssh-ed25519 " + KeyA);

        var file = new KnownHostsFile(FilePath);
        Assert.True(file.Verify("ts.example.com", 10022, "ssh-ed25519", KeyA.TrimEnd('=') + "=").Trusted);
        file.Verify("second.example.com", 10022, "ssh-rsa", KeyB);

        Assert.Equal(KeyB, file.Find("second.example.com", 10022));
        Assert.Equal(KeyA, file.Find("ts.example.com", 10022));
    }

    [Fact]
    public async Task Servers_met_at_the_same_time_are_all_recorded_intact()
    {
        var file = new KnownHostsFile(FilePath);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(index => Task.Run(
            () => file.Verify($"host{index}.example.com", 10022, "ssh-ed25519", KeyA),
            TestContext.Current.CancellationToken)));

        var lines = File.ReadAllLines(FilePath);
        Assert.Equal(20, lines.Length);
        Assert.All(lines, line => Assert.Matches(@"^host\d+\.example\.com:10022 ssh-ed25519 SHA256:\S+$", line));
    }

    [Theory]
    [InlineData(KeyA)]
    [InlineData("ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og")]
    [InlineData("sha256:ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og=")]
    public void A_pinned_key_is_trusted_however_it_was_written(string pinned) =>
        Assert.True(new PinnedHostKey(pinned, "setting").Verify("h", 10022, "ssh-ed25519", KeyA).Trusted);

    [Fact]
    public void A_pinned_key_refuses_any_other_and_names_the_setting()
    {
        var verdict = new PinnedHostKey(KeyA, "TeamSpeak:Profiles:prod:HostKeyFingerprint").Verify("h", 10022, "ssh-ed25519", KeyB);

        Assert.False(verdict.Trusted);
        Assert.Contains("TeamSpeak:Profiles:prod:HostKeyFingerprint", verdict.Reason, StringComparison.Ordinal);
    }
}