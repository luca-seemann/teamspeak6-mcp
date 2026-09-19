using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Mcp.Safety;
using TeamSpeak.Mcp.Tools;
using TeamSpeak.Query.Client;
using TeamSpeak.Query.Transport;

namespace TeamSpeak.Mcp.IntegrationTests;

/// <summary>
/// Subscribes to events on a live TeamSpeak 6 server through a dedicated SSH session, causes events
/// with the tools, and reads them back.
/// </summary>
[Collection(LiveServerDefinition.Name)]
public sealed class EventIntegrationTests(LiveServerFixture server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unique(string what) => $"mcp {what} {Guid.NewGuid().ToString("N")[..8]}";

    private QueryConnectionManager Connections() =>
        new(
            new ProfileRegistry([LiveServerFixture.Profile()]),
            (_, _, _) => Task.FromResult<IQueryTransport>(new ToolIntegrationTests.BorrowedTransport(server.Ssh)));

    [RequiresTeamSpeakServerFact]
    public async Task A_server_message_and_a_new_channel_arrive_as_events_on_their_own_session()
    {
        await using var connections = Connections();
        var executor = new QueryExecutor(connections, new SafetyPolicy(SafetyLevel.Destructive));

        // The real hub, opening its own SSH session to the server.
        await using var hub = new QueryEventHub(connections.Profiles);
        var events = new EventTools(executor, hub);

        var subscribed = await events.SubscribeAsync([EventCategoryName.TextServer, EventCategoryName.Channel], virtualServerId: 1, cancellationToken: Ct);
        Assert.Equal(["channel", "textserver"], subscribed.Subscription.Categories);
        Assert.Equal(1, subscribed.Subscription.SessionsOpened);

        try
        {
            var message = Unique("event");
            await new ClientAdminTools(executor).SendMessageAsync("server", message, virtualServerId: 1, cancellationToken: Ct);

            var text = await WaitForAsync(events, subscribed.Cursor, "textserver", fields => fields.TryGetValue("msg", out var msg) && msg == message);
            Assert.Equal("notifytextmessage", text.Name);

            var channelName = Unique("event");
            var created = await new ChannelAdminTools(executor).CreateAsync(channelName, virtualServerId: 1, cancellationToken: Ct);
            try
            {
                var channel = await WaitForAsync(events, text.Sequence, "channel", fields => fields.TryGetValue("channel_name", out var name) && name == channelName);
                Assert.Equal("notifychannelcreated", channel.Name);
                Assert.Equal(created.Details["cid"], channel.Fields[0]["cid"]);
            }
            finally
            {
                var createdId = int.Parse(created.Details["cid"], System.Globalization.CultureInfo.InvariantCulture);
                await new ChannelAdminTools(executor).DeleteAsync(createdId, await LiveNames.ChannelAsync(executor, createdId), force: true, virtualServerId: 1, cancellationToken: Ct);
            }
        }
        finally
        {
            var ended = await events.UnsubscribeAsync(virtualServerId: 1, cancellationToken: Ct);
            Assert.Null(ended.Remaining);
            Assert.Empty(events.Status().Subscriptions);
        }
    }

    [RequiresTeamSpeakServerFact(Skip = "serverstop is refused until a TeamSpeak version is known that survives it; see KnownCrashes.StopFixedIn.")]
    public async Task A_subscription_recovers_on_its_own_after_its_virtual_server_is_restarted()
    {
        await using var connections = Connections();
        var executor = new QueryExecutor(connections, new SafetyPolicy(SafetyLevel.Destructive));

        // A short watchdog interval so the recovery happens within the test rather than in 30 seconds.
        await using var hub = new QueryEventHub(connections.Profiles, reRegisterInterval: TimeSpan.FromSeconds(2));
        var events = new EventTools(executor, hub);
        var power = new VirtualServerAdminTools(executor);

        var subscribed = await events.SubscribeAsync([EventCategoryName.TextServer], virtualServerId: 1, cancellationToken: Ct);
        try
        {
            // A transfer left over from the file tests makes the stop hang on 6.0.0-beta13, and this
            // server refuses a stop while one is pending. They lapse by themselves: an unused ticket
            // after about two minutes, an upload that broke off after about thirty seconds.
            await WaitForQuietTransfersAsync(executor);

            await power.PowerAsync("stop", 1, "event recovery test", cancellationToken: Ct);
            await power.PowerAsync("start", 1, cancellationToken: Ct);

            // Give the watchdog a few passes to re-register on the restarted virtual server.
            var message = Unique("recovered");
            var arrived = false;
            var cursor = subscribed.Cursor;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (!arrived && DateTimeOffset.UtcNow < deadline)
            {
                await new ClientAdminTools(executor).SendMessageAsync("server", message, virtualServerId: 1, cancellationToken: Ct);
                var page = await events.WaitAsync(cursor, timeoutSeconds: 5, categories: [EventCategoryName.TextServer], virtualServerId: 1, cancellationToken: Ct);
                arrived = page.Events.Any(e => e.Fields.Count > 0 && e.Fields[0].TryGetValue("msg", out var text) && text == message);
                cursor = page.NextCursor;
            }

            Assert.True(arrived, "the subscription did not recover after the virtual server was restarted");
        }
        finally
        {
            await events.UnsubscribeAsync(virtualServerId: 1, cancellationToken: Ct);
        }
    }

    /// <summary>Waits until the virtual server has no file transfer running or waiting.</summary>
    /// <param name="executor">The path to the server.</param>
    /// <remarks>
    /// On 6.0.0-beta13 a pending transfer makes <c>serverstop</c> hang for good, so a test that stops
    /// a server has to let the earlier file tests' transfers lapse first.
    /// </remarks>
    private static async Task WaitForQuietTransfersAsync(QueryExecutor executor)
    {
        var files = new FileTools(executor, new FileTransferOptions());
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var running = await files.ListTransfersAsync(virtualServerId: 1, cancellationToken: Ct);
            if (running.Transfers.Count == 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), Ct);
        }

        Assert.Fail("File transfers were still pending after three minutes, and stopping the server would hang it.");
    }

    /// <summary>Waits until an event of a category with matching fields arrives, reading on from a cursor.</summary>
    private static async Task<EventView> WaitForAsync(EventTools events, long after, string category, Func<IReadOnlyDictionary<string, string>, bool> matches)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        var cursor = after;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var page = await events.WaitAsync(cursor, timeoutSeconds: 5, categories: [Enum.Parse<EventCategoryName>(category, ignoreCase: true)], virtualServerId: 1, cancellationToken: Ct);
            if (page.Events.FirstOrDefault(e => e.Fields.Count > 0 && matches(e.Fields[0])) is { } found)
            {
                return found;
            }

            cursor = page.NextCursor;
        }

        Assert.Fail($"No matching {category} event arrived within 20 seconds.");
        return null!;
    }
}