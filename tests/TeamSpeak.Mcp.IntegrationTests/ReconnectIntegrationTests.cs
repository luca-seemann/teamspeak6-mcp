using System.Diagnostics;

using TeamSpeak.Query.Client;
using TeamSpeak.Query.Protocol;
using TeamSpeak.Query.Transport;

namespace TeamSpeak.Mcp.IntegrationTests;

/// <summary>
/// Breaks a live SSH session in the middle of a command and checks that the transport recovers.
/// </summary>
/// <remarks>
/// Each test routes a session of its own through a <see cref="SeverableTcpProxy"/>, so the shared
/// session is never touched, and each test opens exactly two connections: the one it breaks and the
/// replacement.
/// </remarks>
[Collection(LiveServerDefinition.Name)]
public sealed class ReconnectIntegrationTests
{
    [RequiresTeamSpeakServerFact]
    public async Task A_connection_reset_mid_command_fails_that_command_promptly_and_the_next_one_reconnects()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var proxy = new SeverableTcpProxy(LiveServer.Host, 10022);
        await using var ssh = await SshQueryTransport.ConnectAsync(ProfileThrough(proxy), ct);

        Assert.True((await ssh.SendAsync(new QueryCommand("whoami"), ct)).Error.IsSuccess);

        proxy.CutAfterNextClientWrite();
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(() => ssh.SendAsync(new QueryCommand("serverinfo", VirtualServerId: 1), ct));
        watch.Stop();

        Assert.Equal(1, proxy.Broken);
        TestContext.Current.SendDiagnosticMessage($"reset surfaced after {watch.Elapsed.TotalSeconds:F1} s");

        // The default command timeout is 30 seconds. A reset is known to the SSH layer at once, so the
        // caller must not sit out the whole timeout for an answer that can no longer come.
        Assert.True(
            watch.Elapsed < TimeSpan.FromSeconds(10),
            $"The reset surfaced only after {watch.Elapsed.TotalSeconds:F1} s.");

        await AssertRecoversAsync(ssh, proxy, ct);
    }

    [RequiresTeamSpeakServerFact]
    public async Task A_connection_gone_silent_mid_command_times_out_and_the_next_command_reconnects()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var proxy = new SeverableTcpProxy(LiveServer.Host, 10022);

        var profile = ProfileThrough(proxy);
        profile.CommandTimeout = TimeSpan.FromSeconds(5);
        await using var ssh = await SshQueryTransport.ConnectAsync(profile, ct);

        Assert.True((await ssh.SendAsync(new QueryCommand("whoami"), ct)).Error.IsSuccess);

        // Silence is indistinguishable from a slow answer, so only the command timeout can end the wait.
        proxy.SilenceAfterNextClientWrite();
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(() => ssh.SendAsync(new QueryCommand("serverinfo", VirtualServerId: 1), ct));
        watch.Stop();

        Assert.Equal(1, proxy.Broken);
        TestContext.Current.SendDiagnosticMessage($"silence surfaced after {watch.Elapsed.TotalSeconds:F1} s");
        Assert.True(
            watch.Elapsed < TimeSpan.FromSeconds(10),
            $"The silent connection surfaced only after {watch.Elapsed.TotalSeconds:F1} s.");

        await AssertRecoversAsync(ssh, proxy, ct);
    }

    private static QueryProfile ProfileThrough(SeverableTcpProxy proxy)
    {
        var profile = LiveServerFixture.Profile();
        profile.Host = "127.0.0.1";
        profile.SshPort = proxy.Port;

        // A long interval keeps the keepalive from sending a command of its own that could be the one
        // broken. Its check still runs every few seconds, so after the break either it or the next
        // command reopens the session; both go through the same reconnect path.
        profile.KeepAliveInterval = TimeSpan.FromMinutes(10);
        return profile;
    }

    private static async Task AssertRecoversAsync(SshQueryTransport ssh, SeverableTcpProxy proxy, CancellationToken ct)
    {
        // A scoped command after the break: the replacement session has nothing selected and must say so.
        var info = await ssh.SendAsync(new QueryCommand("serverinfo", VirtualServerId: 1), ct);
        Assert.True(info.Error.IsSuccess, info.Error.Message);
        Assert.Equal("1", Assert.Single(info.Records).GetRequired("virtualserver_id"));

        // Nothing from the broken session may leak into the replacement's answers.
        var whoami = await ssh.SendAsync(new QueryCommand("whoami"), ct);
        Assert.True(whoami.Error.IsSuccess, whoami.Error.Message);
        Assert.Equal("serveradmin", Assert.Single(whoami.Records).GetRequired("client_login_name"));

        Assert.Equal(2, proxy.Accepted);
    }
}