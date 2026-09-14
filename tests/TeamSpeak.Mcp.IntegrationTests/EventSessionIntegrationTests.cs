using TeamSpeak.Query.Protocol;
using TeamSpeak.Query.Transport;

namespace TeamSpeak.Mcp.IntegrationTests;

/// <summary>
/// Checks against a live server that a session opened for events registers again when it is replaced.
/// </summary>
[Collection(LiveServerDefinition.Name)]
public sealed class EventSessionIntegrationTests(LiveServerFixture server)
{
    [RequiresTeamSpeakServerFact]
    public async Task A_replaced_session_registers_again_and_keeps_delivering_events()
    {
        var ct = TestContext.Current.CancellationToken;
        var opened = 0;

        var profile = LiveServerFixture.Profile();
        // A quit may never be answered, so do not wait the default 30 seconds for it.
        profile.CommandTimeout = TimeSpan.FromSeconds(5);

        await using var events = await SshQueryTransport.ConnectAsync(
            profile,
            async (send, token) =>
            {
                Interlocked.Increment(ref opened);
                var registered = await send(
                    new QueryCommand("servernotifyregister", new Dictionary<string, string> { ["event"] = "textserver" }, VirtualServerId: 1),
                    token);

                if (!registered.Error.IsSuccess)
                {
                    throw new QueryProtocolException($"servernotifyregister was refused: {registered.Error.Message}");
                }
            },
            ct);

        Assert.Equal(1, opened);

        // End the session from the server's side, as a dropped connection would.
        try
        {
            await events.SendAsync(new QueryCommand("quit"), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The server may close the connection before it answers.
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (Volatile.Read(ref opened) < 2 && DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await events.SendAsync(new QueryCommand("version"), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // A send on the dead session fails; the next one opens a new session.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }

        Assert.Equal(2, Volatile.Read(ref opened));

        // A message sent from the shared session now has to arrive on the replacement.
        var message = $"mcp reconnect {Guid.NewGuid().ToString("N")[..8]}";
        var reading = WaitForMessageAsync(events, message, ct);

        var sent = await server.Ssh.SendAsync(
            new QueryCommand("sendtextmessage", new Dictionary<string, string> { ["targetmode"] = "3", ["target"] = "1", ["msg"] = message }, VirtualServerId: 1),
            ct);
        Assert.True(sent.Error.IsSuccess, sent.Error.Message);

        Assert.True(await reading, $"The message '{message}' did not arrive on the replaced session within 15 seconds.");
    }

    private static async Task<bool> WaitForMessageAsync(SshQueryTransport events, string message, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            await foreach (var notification in events.GetEventsAsync(deadline.Token))
            {
                if (notification.Name == "notifytextmessage"
                    && notification.Records.Count > 0
                    && notification.Records[0].TryGetValue("msg", out var text)
                    && text == message)
                {
                    return true;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timed out.
        }

        return false;
    }
}