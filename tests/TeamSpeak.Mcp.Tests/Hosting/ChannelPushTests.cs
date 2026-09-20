using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Mcp.Hosting;
using TeamSpeak.Query.Client;
using TeamSpeak.Query.Transport;

namespace TeamSpeak.Mcp.Tests.Hosting;

/// <summary>
/// What a channel pushes into a model's context, and what it keeps out of it.
/// </summary>
public class ChannelPushTests
{
    [Fact]
    public void Chat_from_an_unknown_writer_is_not_pushed()
    {
        var push = new ChannelPush(new ChannelOptions { Enabled = true });

        Assert.False(push.MayPush(Chat("someone-else-uid")));
    }

    [Fact]
    public void Chat_from_a_listed_writer_is_pushed()
    {
        var push = new ChannelPush(new ChannelOptions { Enabled = true, AllowedSenders = ["trusted-uid"] });

        Assert.True(push.MayPush(Chat("trusted-uid")));
    }

    [Fact]
    public void Chat_without_a_writer_is_not_pushed()
    {
        var push = new ChannelPush(new ChannelOptions { Enabled = true, AllowedSenders = ["trusted-uid"] });

        Assert.False(push.MayPush(new QueryEvent("notifytextmessage", [Fields(("msg", "hello"))], DateTimeOffset.UtcNow)));
    }

    [Fact]
    public void Everything_but_chat_is_pushed_without_a_list()
    {
        var push = new ChannelPush(new ChannelOptions { Enabled = true });

        Assert.True(push.MayPush(new QueryEvent("notifycliententerview", [Fields(("client_nickname", "Alice"))], DateTimeOffset.UtcNow)));
    }

    [Theory]
    [InlineData("notifytextmessage", "invokername", "Alice", "Alice wrote: hello")]
    [InlineData("notifycliententerview", "client_nickname", "Bob", "Bob connected")]
    public void The_line_the_model_reads_names_the_person_and_what_happened(
        string name, string field, string who, string expected)
    {
        var notification = new QueryEvent(name, [Fields((field, who), ("msg", "hello"))], DateTimeOffset.UtcNow);

        Assert.Equal(expected, ChannelPush.Content(new BufferedEvent(1, 1, notification)));
    }

    [Fact]
    public void An_event_nobody_wrote_a_line_for_still_says_what_it_was()
    {
        var notification = new QueryEvent("notifychanneledited", [Fields(("cid", "12"))], DateTimeOffset.UtcNow);

        Assert.Equal("notifychanneledited cid=12", ChannelPush.Content(new BufferedEvent(3, 1, notification)));
    }

    [Fact]
    public void The_attributes_are_identifiers_the_client_keeps()
    {
        var notification = new QueryEvent("notifyclientmoved", [Fields(("ctid", "7"))], DateTimeOffset.UtcNow);

        var meta = ChannelPush.Meta("home", new BufferedEvent(42, 1, notification));

        Assert.Equal("home", meta["profile"]);
        Assert.Equal("1", meta["virtual_server"]);
        Assert.Equal("notifyclientmoved", meta["event"]);
        Assert.Equal("42", meta["sequence"]);
        Assert.All(meta.Keys, key => Assert.Matches("^[A-Za-z0-9_]+$", key));
    }

    private static QueryEvent Chat(string writer) =>
        new("notifytextmessage", [Fields(("invokeruid", writer), ("msg", "hello"))], DateTimeOffset.UtcNow);

    private static Dictionary<string, string> Fields(params (string Key, string Value)[] fields) =>
        fields.ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal);
}