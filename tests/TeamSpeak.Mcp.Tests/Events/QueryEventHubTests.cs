using TeamSpeak.Query.Client;
using TeamSpeak.Query.FakeServer;
using TeamSpeak.Query.Protocol;
using TeamSpeak.Query.Transport;

namespace TeamSpeak.Mcp.Tests.Events;

public class QueryEventHubTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static QueryResponse Ok => new([], new QueryError(0, "ok"));

    private static ProfileRegistry Registry(string? password = "secret") =>
        new([new QueryProfile { Name = "test", Host = "ts.example.com", Password = password, ApiKey = password is null ? "key" : null, WebQueryUrl = password is null ? new Uri("http://ts.example.com:10080") : null }]);

    /// <summary>A hub whose event sessions are fakes; each opened session is recorded with its registration callback.</summary>
    private sealed class Sessions
    {
        public List<(FakeQueryTransport Transport, Func<QuerySender, CancellationToken, Task> OnOpened)> Opened { get; } = [];

        public async Task<IQueryTransport> OpenAsync(QueryProfile profile, Func<QuerySender, CancellationToken, Task> onOpened, CancellationToken cancellationToken)
        {
            var transport = new FakeQueryTransport()
                .Returns("servernotifyregister", Ok)
                .Returns("servernotifyunregister", Ok);

            // As the real transport does: register on the fresh session before handing it out.
            await onOpened(transport.SendAsync, cancellationToken);
            Opened.Add((transport, onOpened));
            return transport;
        }
    }

    private static string Registration(QueryCommand command) =>
        command.Parameters!["event"] + (command.Parameters.TryGetValue("id", out var id) ? $" id={id}" : string.Empty);

    [Fact]
    public async Task Opens_one_session_per_virtual_server_and_registers_every_category_on_it()
    {
        var sessions = new Sessions();
        await using var hub = new QueryEventHub(Registry(), 100, sessions.OpenAsync);

        var subscription = await hub.SubscribeAsync("test", 1, [EventCategory.TextServer, EventCategory.Channel], channelId: 0, Ct);

        var (transport, _) = Assert.Single(sessions.Opened);
        Assert.Equal(["textserver", "channel id=0"], transport.SentCommands.Select(Registration));
        Assert.All(transport.SentCommands, command => Assert.Equal(1, command.VirtualServerId));
        Assert.Equal([EventCategory.Channel, EventCategory.TextServer], subscription.Categories);
        Assert.Equal(1, subscription.SessionsOpened);
    }

    [Fact]
    public async Task Adding_categories_registers_only_the_new_ones_on_the_open_session()
    {
        var sessions = new Sessions();
        await using var hub = new QueryEventHub(Registry(), 100, sessions.OpenAsync);

        await hub.SubscribeAsync("test", 1, [EventCategory.TextServer], cancellationToken: Ct);
        var subscription = await hub.SubscribeAsync("test", 1, [EventCategory.TextServer, EventCategory.Bans], cancellationToken: Ct);

        var (transport, _) = Assert.Single(sessions.Opened);
        Assert.Equal(["textserver", "bans"], transport.SentCommands.Select(Registration));
        Assert.Equal([EventCategory.TextServer, EventCategory.Bans], subscription.Categories);
    }

    [Fact]
    public async Task A_reconnect_registers_everything_again_and_is_counted()
    {
        var sessions = new Sessions();
        await using var hub = new QueryEventHub(Registry(), 100, sessions.OpenAsync);
        await hub.SubscribeAsync("test", 1, [EventCategory.TextServer, EventCategory.Bans], cancellationToken: Ct);

        var (transport, onOpened) = Assert.Single(sessions.Opened);
        var before = transport.SentCommands.Count;

        // What the SSH transport does after replacing a dropped session.
        await onOpened(transport.SendAsync, Ct);

        Assert.Equal(["bans", "textserver"], transport.SentCommands.Skip(before).Select(Registration).Order());
        Assert.Equal(2, Assert.Single(hub.Subscriptions("test")).SessionsOpened);
    }

    [Fact]
    public async Task Events_from_the_session_land_in_the_profile_buffer_tagged_with_their_virtual_server()
    {
        var sessions = new Sessions();
        await using var hub = new QueryEventHub(Registry(), 100, sessions.OpenAsync);
        await hub.SubscribeAsync("test", 3, [EventCategory.TextServer], cancellationToken: Ct);

        var (transport, _) = Assert.Single(sessions.Opened);
        transport.Push(new QueryEvent("notifytextmessage", [new Dictionary<string, string> { ["targetmode"] = "3", ["msg"] = "hello" }], DateTimeOffset.UtcNow));

        var page = await hub.BufferFor("test").WaitAsync(0, 10, TimeSpan.FromSeconds(5), cancellationToken: Ct);

        var arrived = Assert.Single(page.Events);
        Assert.Equal((3, "hello"), (arrived.VirtualServerId, arrived.Event.Records[0]["msg"]));
    }

    [Fact]
    public async Task Unsubscribing_some_categories_unregisters_them_and_the_last_one_closes_the_session()
    {
        var sessions = new Sessions();
        await using var hub = new QueryEventHub(Registry(), 100, sessions.OpenAsync);
        await hub.SubscribeAsync("test", 1, [EventCategory.TextServer, EventCategory.Bans], cancellationToken: Ct);
        var (transport, _) = Assert.Single(sessions.Opened);

        var remaining = await hub.UnsubscribeAsync("test", 1, [EventCategory.Bans], Ct);

        Assert.Equal([EventCategory.TextServer], remaining!.Categories);
        Assert.Equal(("servernotifyunregister", "bans"), (transport.SentCommands[^1].Name, transport.SentCommands[^1].Parameters!["event"]));
        Assert.False(transport.IsDisposed);

        Assert.Null(await hub.UnsubscribeAsync("test", 1, cancellationToken: Ct));
        Assert.True(transport.IsDisposed);
        Assert.Empty(hub.Subscriptions("test"));
    }

    [Fact]
    public async Task A_different_channel_replaces_the_channel_registration()
    {
        var sessions = new Sessions();
        await using var hub = new QueryEventHub(Registry(), 100, sessions.OpenAsync);
        await hub.SubscribeAsync("test", 1, [EventCategory.Channel], channelId: 0, Ct);

        var subscription = await hub.SubscribeAsync("test", 1, [EventCategory.Channel], channelId: 7, Ct);

        var (transport, _) = Assert.Single(sessions.Opened);
        Assert.Equal(
            ["servernotifyregister channel id=0", "servernotifyunregister channel", "servernotifyregister channel id=7"],
            transport.SentCommands.Select(command => $"{command.Name} {Registration(command)}"));
        Assert.Equal(7, subscription.ChannelId);
    }

    [Fact]
    public async Task A_refused_registration_fails_the_subscription_and_leaves_nothing_subscribed()
    {
        await using var hub = new QueryEventHub(Registry(), 100, async (profile, onOpened, token) =>
        {
            var transport = new FakeQueryTransport().Returns("servernotifyregister", new QueryResponse([], new QueryError(2568, "insufficient client permissions")));
            await onOpened(transport.SendAsync, token);
            return transport;
        });

        var refused = await Assert.ThrowsAsync<QueryProtocolException>(() => hub.SubscribeAsync("test", 1, [EventCategory.Bans], cancellationToken: Ct));

        Assert.Contains("insufficient client permissions", refused.Message, StringComparison.Ordinal);
        Assert.Empty(hub.Subscriptions("test"));
    }

    [Fact]
    public async Task A_profile_without_ssh_is_refused_before_anything_is_opened()
    {
        var sessions = new Sessions();
        await using var hub = new QueryEventHub(Registry(password: null), 100, sessions.OpenAsync);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => hub.SubscribeAsync("test", 1, [EventCategory.Server], cancellationToken: Ct));

        Assert.Contains("SSH", refused.Message, StringComparison.Ordinal);
        Assert.Empty(sessions.Opened);
    }

    [Fact]
    public async Task Disposing_closes_every_event_session()
    {
        var sessions = new Sessions();
        var hub = new QueryEventHub(Registry(), 100, sessions.OpenAsync);
        await hub.SubscribeAsync("test", 1, [EventCategory.Server], cancellationToken: Ct);
        await hub.SubscribeAsync("test", 2, [EventCategory.Server], cancellationToken: Ct);

        await hub.DisposeAsync();

        Assert.All(sessions.Opened, opened => Assert.True(opened.Transport.IsDisposed));
    }

    [Theory]
    [InlineData("notifytextmessage", "3", "TextServer")]
    [InlineData("notifytextmessage", "2", "TextChannel")]
    [InlineData("notifytextmessage", "1", "TextPrivate")]
    [InlineData("notifybanupdate", null, "Bans")]
    [InlineData("notifyserveredited", null, "Server")]
    [InlineData("notifyclientmoved", null, "Channel")]
    [InlineData("notifychannelcreated", null, "Channel")]
    [InlineData("notifychanneldeleted", null, "Channel")]
    [InlineData("notifycliententerview", null, "Server,Channel")]
    [InlineData("notifyclientleftview", null, "Server,Channel")]
    [InlineData("notifysomethingnew", null, "")]
    public void Sorts_events_into_the_categories_that_deliver_them(string name, string? targetMode, string expected)
    {
        var records = targetMode is null
            ? new List<IReadOnlyDictionary<string, string>> { new Dictionary<string, string>() }
            : [new Dictionary<string, string> { ["targetmode"] = targetMode }];

        var categories = QueryEventHub.CategoriesOf(new QueryEvent(name, records, DateTimeOffset.UtcNow));

        Assert.Equal(expected, string.Join(',', categories));
    }
}