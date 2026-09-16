using ModelContextProtocol;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Mcp.Tests.Tools;
using TeamSpeak.Mcp.Tools;
using TeamSpeak.Query.Client;
using TeamSpeak.Query.FakeServer;
using TeamSpeak.Query.Protocol;
using TeamSpeak.Query.Transport;

namespace TeamSpeak.Mcp.Tests.Events;

public class EventToolsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Event sessions as fakes, so a test can push events into them.</summary>
    private sealed class Sessions
    {
        public List<FakeQueryTransport> Opened { get; } = [];

        public async Task<IQueryTransport> OpenAsync(QueryProfile profile, Func<QuerySender, CancellationToken, Task> onOpened, CancellationToken cancellationToken)
        {
            var transport = new FakeQueryTransport()
                .Returns("servernotifyregister", ToolHarness.Records())
                .Returns("servernotifyunregister", ToolHarness.Records())
                .Returns("whoami", ToolHarness.Records(new Dictionary<string, string> { ["client_id"] = "17" }));
            await onOpened(transport.SendAsync, cancellationToken);
            Opened.Add(transport);
            return transport;
        }
    }

    private static QueryEvent Text(string mode, string message) =>
        new("notifytextmessage", [new Dictionary<string, string> { ["targetmode"] = mode, ["msg"] = message }], DateTimeOffset.UtcNow);

    [Fact]
    public async Task Subscribing_without_categories_takes_all_of_them_and_returns_the_cursor_to_read_from()
    {
        await using var harness = new ToolHarness();
        var sessions = new Sessions();
        await using var hub = new QueryEventHub(harness.Executor.Connections.Profiles, 100, sessions.OpenAsync);
        hub.BufferFor("test").Append(1, Text("3", "before subscribing"));

        var result = await new EventTools(harness.Executor, hub).SubscribeAsync(cancellationToken: Ct);

        Assert.Equal(["server", "channel", "textserver", "textchannel", "textprivate", "bans"], result.Subscription.Categories);
        Assert.Equal((1, 1L, 17), (result.Subscription.VirtualServerId, result.Cursor, result.Subscription.ClientId));
        Assert.Equal(6, Assert.Single(sessions.Opened).SentCommands.Count(command => command.Name == "servernotifyregister"));
    }

    [Fact]
    public async Task Polling_filters_by_category_and_virtual_server_and_moves_the_cursor_past_the_rest()
    {
        await using var harness = new ToolHarness();
        await using var hub = new QueryEventHub(harness.Executor.Connections.Profiles, 100, new Sessions().OpenAsync);
        var buffer = hub.BufferFor("test");
        buffer.Append(1, Text("3", "server message"));
        buffer.Append(1, Text("1", "private message"));
        buffer.Append(2, Text("3", "other server"));

        var result = new EventTools(harness.Executor, hub).Poll(after: 0, categories: [EventCategoryName.TextServer], virtualServerId: 1);

        var only = Assert.Single(result.Events);
        Assert.Equal(("server message", "notifytextmessage", "textserver"), (only.Fields[0]["msg"], only.Name, Assert.Single(only.Categories)));
        Assert.Equal(3, result.NextCursor);
    }

    [Fact]
    public async Task Polling_with_nothing_subscribed_says_so()
    {
        await using var harness = new ToolHarness();
        await using var hub = new QueryEventHub(harness.Executor.Connections.Profiles, 100, new Sessions().OpenAsync);

        var result = new EventTools(harness.Executor, hub).Poll();

        Assert.Empty(result.Events);
        Assert.Contains(result.Notes, note => note.Contains("ts_events_subscribe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Waiting_returns_the_event_that_arrives_on_the_session()
    {
        await using var harness = new ToolHarness();
        var sessions = new Sessions();
        await using var hub = new QueryEventHub(harness.Executor.Connections.Profiles, 100, sessions.OpenAsync);
        var tools = new EventTools(harness.Executor, hub);
        var subscribed = await tools.SubscribeAsync([EventCategoryName.TextServer], cancellationToken: Ct);

        var waiting = tools.WaitAsync(subscribed.Cursor, timeoutSeconds: 10, cancellationToken: Ct);
        Assert.Single(sessions.Opened).Push(Text("3", "hello"));
        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal("hello", Assert.Single(result.Events).Fields[0]["msg"]);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public async Task Unsubscribing_everything_closes_the_session()
    {
        await using var harness = new ToolHarness();
        var sessions = new Sessions();
        await using var hub = new QueryEventHub(harness.Executor.Connections.Profiles, 100, sessions.OpenAsync);
        var tools = new EventTools(harness.Executor, hub);
        await tools.SubscribeAsync([EventCategoryName.Bans], cancellationToken: Ct);

        var result = await tools.UnsubscribeAsync(cancellationToken: Ct);

        Assert.Null(result.Remaining);
        Assert.True(Assert.Single(sessions.Opened).IsDisposed);
        Assert.Empty(tools.Status().Subscriptions);
    }

    [Fact]
    public async Task An_unknown_category_is_refused_before_anything_is_opened()
    {
        await using var harness = new ToolHarness();
        var sessions = new Sessions();
        await using var hub = new QueryEventHub(harness.Executor.Connections.Profiles, 100, sessions.OpenAsync);

        await Assert.ThrowsAsync<McpException>(() => new EventTools(harness.Executor, hub).SubscribeAsync([(EventCategoryName)99], cancellationToken: Ct));

        Assert.Empty(sessions.Opened);
    }

    [Fact]
    public void The_tool_categories_name_exactly_the_hub_categories()
    {
        Assert.Equal(Enum.GetNames<EventCategory>(), Enum.GetNames<EventCategoryName>());

        // The schema's lowercase names, which the subscription result reports back the same way.
        Assert.Equal(
            ["\"server\"", "\"channel\"", "\"textserver\"", "\"textchannel\"", "\"textprivate\"", "\"bans\""],
            Enum.GetValues<EventCategoryName>().Select(category => System.Text.Json.JsonSerializer.Serialize(category)));
    }

    [Fact]
    public async Task A_webquery_only_profile_is_told_that_events_need_ssh()
    {
        var profile = new QueryProfile { Name = "test", Host = "ts.example.com", ApiKey = "key", WebQueryUrl = new Uri("http://ts.example.com:10080") };
        await using var harness = new ToolHarness(SafetyLevel.ReadOnly, profile, holdsSession: false);
        await using var hub = new QueryEventHub(harness.Executor.Connections.Profiles, 100, new Sessions().OpenAsync);

        var refused = await Assert.ThrowsAsync<McpException>(() => new EventTools(harness.Executor, hub).SubscribeAsync(cancellationToken: Ct));

        Assert.Contains("SSH", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_reports_the_buffer_and_each_subscription()
    {
        await using var harness = new ToolHarness();
        await using var hub = new QueryEventHub(harness.Executor.Connections.Profiles, 42, new Sessions().OpenAsync);
        var tools = new EventTools(harness.Executor, hub);
        await tools.SubscribeAsync([EventCategoryName.Server], virtualServerId: 1, cancellationToken: Ct);
        hub.BufferFor("test").Append(1, Text("3", "x"));

        var status = tools.Status();

        Assert.Equal((42, 1L, 1L), (status.BufferCapacity, status.Oldest, status.Newest));
        Assert.Equal(["server"], Assert.Single(status.Subscriptions).Categories);
    }
}