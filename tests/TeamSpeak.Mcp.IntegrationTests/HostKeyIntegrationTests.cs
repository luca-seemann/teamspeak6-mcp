using System.Diagnostics;

using TeamSpeak.Query.Protocol;
using TeamSpeak.Query.Transport;

namespace TeamSpeak.Mcp.IntegrationTests;

/// <summary>
/// The SSH host key check against the live server: remembered on first use, enforced when pinned.
/// </summary>
/// <remarks>
/// Three connections in one test, spaced by the connection throttle, so the server's flood
/// protection is not provoked.
/// </remarks>
[Collection(LiveServerDefinition.Name)]
public sealed class HostKeyIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tsmcp-live-hostkeys-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [RequiresTeamSpeakServerFact]
    public async Task The_first_key_is_remembered_a_wrong_pin_is_refused_at_once_and_the_right_pin_connects()
    {
        var ct = TestContext.Current.CancellationToken;
        var knownHosts = new KnownHostsFile(Path.Combine(_directory, "known_hosts"));

        var profile = LiveServerFixture.Profile();
        profile.HostKeyVerifier = knownHosts;
        await using (var first = await SshQueryTransport.ConnectAsync(profile, ct))
        {
            Assert.True((await first.SendAsync(new QueryCommand("version"), ct)).Error.IsSuccess);
        }

        var remembered = knownHosts.Find(profile.Host, profile.SshPort);
        Assert.NotNull(remembered);
        Assert.StartsWith("SHA256:", remembered, StringComparison.Ordinal);

        var wrong = LiveServerFixture.Profile();
        wrong.HostKeyFingerprint = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var clock = Stopwatch.StartNew();

        var refused = await Assert.ThrowsAsync<SshHostKeyMismatchException>(() => SshQueryTransport.ConnectAsync(wrong, ct));

        // One attempt, no reconnect loop: a refusal comes back within the handshake, not after backoffs.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"Refusing took {clock.Elapsed}.");
        Assert.Contains(remembered, refused.Message, StringComparison.Ordinal);
        Assert.Contains("HostKeyFingerprint", refused.Message, StringComparison.Ordinal);

        var pinned = LiveServerFixture.Profile();
        pinned.HostKeyFingerprint = remembered;
        await using var connected = await SshQueryTransport.ConnectAsync(pinned, ct);
        Assert.True((await connected.SendAsync(new QueryCommand("version"), ct)).Error.IsSuccess);
    }
}