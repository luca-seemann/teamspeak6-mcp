using System.ComponentModel;
using System.Text.Json.Serialization;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Query.Client;
using TeamSpeak.Query.Protocol;

using static TeamSpeak.Mcp.Tools.ToolArguments;

namespace TeamSpeak.Mcp.Tools;

/// <summary>Tools for receiving what happens on a virtual server as it happens.</summary>
/// <param name="executor">The shared path to the server, used for profiles and safety.</param>
/// <param name="hub">The event subscriptions and their buffers.</param>
[McpServerToolType]
public sealed class EventTools(QueryExecutor executor, QueryEventHub hub)
{
    private const int MaxWaitSeconds = 60;

    /// <summary>Subscribes to events.</summary>
    /// <param name="categories">The categories.</param>
    /// <param name="channelId">One channel for the channel category.</param>
    /// <param name="textChannelId">The channel to move the event session into for textchannel messages.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The subscription and the cursor to read from.</returns>
    [McpServerTool(Name = "ts_events_subscribe", Title = "Subscribe to server events",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Starts collecting events from a virtual server for ts_events_poll and ts_events_wait. " +
                 "Categories: server (clients connecting and leaving, settings changed), channel (channels " +
                 "changed, clients moving, connecting and leaving), textserver (server chat), textchannel " +
                 "(chat in the event session's channel), textprivate (private messages to the event " +
                 "session's clientId, reported in the result) and bans. Omitted, all of them; new " +
                 "categories add to those subscribed. Pass the returned cursor as 'after' to read only " +
                 "what arrives from now on. Needs SSH and opens a query session of its own. Subscriptions " +
                 "are shared by everyone using this server until ts_events_unsubscribe or a restart.")]
    public async Task<EventSubscribeResult> SubscribeAsync(
        [Description("Categories to add. Omit for all.")] EventCategoryName[]? categories = null,
        [Description("Limit the channel category to one channel id; omit for every channel.")] int? channelId = null,
        [Description("For textchannel: the channel whose chat to receive. The event session moves there, shows as a query client, and returns there after a reconnect; moving it needs Write. Omitted, it stays where it is, at first the default channel.")]
        int? textChannelId = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        // Registering for events changes nothing others see, but moving the event session into a channel
        // is a clientmove: the session shows up there as a query client.
        executor.Demand("ts_events_subscribe", textChannelId is null ? SafetyLevel.ReadOnly : SafetyLevel.Write, profile);
        var resolved = executor.ResolveProfile(profile);
        var serverId = virtualServerId ?? resolved.DefaultVirtualServerId;
        var wanted = ParseCategories(categories) ?? Enum.GetValues<EventCategory>();

        var cursor = hub.BufferFor(resolved.Name).Newest;
        var subscription = await Run(
            resolved.Name,
            "Subscribing",
            () => hub.SubscribeAsync(resolved.Name, serverId, wanted, channelId ?? 0, textChannelId ?? 0, cancellationToken)).ConfigureAwait(false);

        return new EventSubscribeResult(
            View(subscription),
            cursor,
            "Pass cursor as 'after' to ts_events_poll or ts_events_wait to receive what arrives from now on, " +
            "or 0 for everything still buffered.");
    }

    /// <summary>Ends subscriptions.</summary>
    /// <param name="categories">The categories to end.</param>
    /// <param name="virtualServerId">The virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What is still subscribed.</returns>
    [McpServerTool(Name = "ts_events_unsubscribe", Title = "Unsubscribe from server events",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Stops collecting some or all event categories from a virtual server. Without " +
                 "categories, or when none would be left, the event session is closed. Events already " +
                 "collected stay readable until newer ones push them out.")]
    public async Task<EventUnsubscribeResult> UnsubscribeAsync(
        [Description("Categories to end. Omit to end all of them.")] EventCategoryName[]? categories = null,
        [Description(ToolDescriptions.VirtualServerId)] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        executor.Demand("ts_events_unsubscribe", SafetyLevel.ReadOnly, profile);
        var resolved = executor.ResolveProfile(profile);
        var serverId = virtualServerId ?? resolved.DefaultVirtualServerId;

        var remaining = await Run(
            resolved.Name,
            "Unsubscribing",
            () => hub.UnsubscribeAsync(resolved.Name, serverId, ParseCategories(categories), cancellationToken)).ConfigureAwait(false);

        return new EventUnsubscribeResult(
            remaining is null
                ? $"Nothing is subscribed on virtual server {serverId} any more; its event session is closed."
                : $"Still subscribed on virtual server {serverId}: {string.Join(", ", remaining.Categories.Select(QueryEventHub.WireName))}.",
            remaining is null ? null : View(remaining));
    }

    /// <summary>Reads collected events.</summary>
    /// <param name="after">The cursor.</param>
    /// <param name="limit">How many to return.</param>
    /// <param name="categories">Only these categories.</param>
    /// <param name="virtualServerId">Only this virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <returns>The events and the next cursor.</returns>
    [McpServerTool(Name = "ts_events_poll", Title = "Read collected server events",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Returns the events collected after a cursor, oldest first, without waiting. Pass the " +
                 "nextCursor of the previous answer as 'after' to continue. Each event has TeamSpeak's " +
                 "notification name, for example notifytextmessage or notifyclientmoved, its categories " +
                 "and its fields. 'missed' counts events that were pushed out of the buffer before they " +
                 "were read. Needs ts_events_subscribe first." + ToolDescriptions.UserWrittenText)]
    public EventPollResult Poll(
        [Description("The last cursor seen: nextCursor from an earlier answer, or the cursor from ts_events_subscribe. 0 for everything still buffered.")]
        long after = 0,
        [Description("How many events to return, from 1 to 200.")] int limit = 50,
        [Description("Only events of these categories. Omit for all.")] EventCategoryName[]? categories = null,
        [Description("Only events from this virtual server; omit for all.")] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null)
    {
        executor.Demand("ts_events_poll", SafetyLevel.ReadOnly, profile);
        var resolved = executor.ResolveProfile(profile);

        var page = hub.BufferFor(resolved.Name).Read(after, Math.Clamp(limit, 1, 200), Filter(ParseCategories(categories), virtualServerId));
        return Result(resolved.Name, page);
    }

    /// <summary>Waits for events.</summary>
    /// <param name="after">The cursor.</param>
    /// <param name="timeoutSeconds">How long to wait.</param>
    /// <param name="limit">How many to return.</param>
    /// <param name="categories">Only these categories.</param>
    /// <param name="virtualServerId">Only this virtual server.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The events and the next cursor.</returns>
    [McpServerTool(Name = "ts_events_wait", Title = "Wait for server events",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Like ts_events_poll, but when nothing new has been collected yet it waits until an " +
                 "event arrives or the timeout passes, at most 60 seconds. An empty answer means nothing " +
                 "happened in that time; pass its nextCursor to wait again." + ToolDescriptions.UserWrittenText)]
    public async Task<EventPollResult> WaitAsync(
        [Description("The last cursor seen: nextCursor from an earlier answer, or the cursor from ts_events_subscribe.")]
        long after,
        [Description("How long to wait, from 1 to 60 seconds.")] int timeoutSeconds = 30,
        [Description("How many events to return, from 1 to 200.")] int limit = 50,
        [Description("Only events of these categories. Omit for all.")] EventCategoryName[]? categories = null,
        [Description("Only events from this virtual server; omit for all.")] int? virtualServerId = null,
        [Description(ToolDescriptions.Profile)] string? profile = null,
        CancellationToken cancellationToken = default)
    {
        executor.Demand("ts_events_wait", SafetyLevel.ReadOnly, profile);
        var resolved = executor.ResolveProfile(profile);

        var page = await hub.BufferFor(resolved.Name).WaitAsync(
            after,
            Math.Clamp(limit, 1, 200),
            TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, MaxWaitSeconds)),
            Filter(ParseCategories(categories), virtualServerId),
            cancellationToken).ConfigureAwait(false);

        return Result(resolved.Name, page);
    }

    /// <summary>Shows subscriptions and the buffer.</summary>
    /// <param name="profile">The profile.</param>
    /// <returns>The status.</returns>
    [McpServerTool(Name = "ts_events_status", Title = "Show event subscriptions",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Shows which event categories are subscribed on which virtual server, since when, and " +
                 "how many sessions have carried each subscription, plus the buffer's size and the " +
                 "cursors of its oldest and newest event. A sessionsOpened above 1 means the connection " +
                 "was replaced and events may have been lost in between.")]
    public EventStatus Status(
        [Description(ToolDescriptions.Profile)] string? profile = null)
    {
        executor.Demand("ts_events_status", SafetyLevel.ReadOnly, profile);
        var resolved = executor.ResolveProfile(profile);
        var buffer = hub.BufferFor(resolved.Name);

        return new EventStatus(
            hub.Subscriptions(resolved.Name).Select(View).ToList(),
            buffer.Capacity,
            buffer.Oldest,
            buffer.Newest);
    }

    private static async Task<T> Run<T>(string profileName, string doing, Func<Task<T>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or QueryProtocolException)
        {
            throw new McpException(ex.Message, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not McpException)
        {
            throw new McpException($"{doing} did not complete on profile '{profileName}': {ex.Message}", ex);
        }
    }

    private static EventCategory[]? ParseCategories(EventCategoryName[]? categories) =>
        categories is null || categories.Length == 0
            ? null
            : categories
                .Select(category => Enum.IsDefined(category)
                    ? Enum.Parse<EventCategory>(category.ToString())
                    : throw new McpException($"'categories' holds an unknown category {(int)category}."))
                .Distinct()
                .ToArray();

    private static Func<BufferedEvent, bool>? Filter(EventCategory[]? categories, int? virtualServerId) =>
        categories is null && virtualServerId is null
            ? null
            : buffered =>
                (virtualServerId is null || buffered.VirtualServerId == virtualServerId)
                && (categories is null || QueryEventHub.CategoriesOf(buffered.Event).Any(categories.Contains));

    private EventPollResult Result(string profileName, EventPage page)
    {
        var subscriptions = hub.Subscriptions(profileName).Select(View).ToList();
        var notes = new List<string>();

        if (subscriptions.Count == 0)
        {
            notes.Add("Nothing is subscribed on this profile, so no new events are being collected. Use ts_events_subscribe.");
        }

        if (page.Missed > 0)
        {
            notes.Add($"{page.Missed} events after your cursor were pushed out of the buffer before they were read.");
        }

        if (page.CursorReset)
        {
            notes.Add("Your cursor was beyond the newest event, probably from before a restart, so reading started over.");
        }

        foreach (var replaced in subscriptions.Where(subscription => subscription.SessionsOpened > 1))
        {
            notes.Add($"The event session of virtual server {replaced.VirtualServerId} has been opened {replaced.SessionsOpened} times; events that arrived while it was reconnecting were lost.");
        }

        return new EventPollResult(
            page.Events.Select(buffered => new EventView(
                buffered.Sequence,
                buffered.Event.ReceivedAt,
                buffered.VirtualServerId,
                buffered.Event.Name,
                QueryEventHub.CategoriesOf(buffered.Event).Select(QueryEventHub.WireName).ToList(),
                buffered.Event.Records)).ToList(),
            page.NextCursor,
            page.Missed,
            page.CursorReset,
            subscriptions,
            notes);
    }

    private static EventSubscriptionView View(EventSubscription subscription) =>
        new(
            subscription.VirtualServerId,
            subscription.Categories.Select(QueryEventHub.WireName).ToList(),
            subscription.ChannelId,
            subscription.TextChannelId,
            subscription.Since,
            subscription.SessionsOpened,
            subscription.ClientId,
            subscription.LastEventAt,
            subscription.LastError,
            subscription.LastError is null);
}

/// <summary>One virtual server's event subscription.</summary>
/// <param name="VirtualServerId">The virtual server.</param>
/// <param name="Categories">The subscribed categories.</param>
/// <param name="ChannelId">The one channel the channel category covers; 0 for every channel.</param>
/// <param name="TextChannelId">The channel the event session sits in for textchannel messages; 0 for the default channel.</param>
/// <param name="Since">When its event session was opened.</param>
/// <param name="SessionsOpened">How many sessions have carried it; above 1 means events may have been lost.</param>
/// <param name="ClientId">The event session's client id: send private messages here to see them as textprivate events.</param>
/// <param name="LastEventAt">When the last event arrived, or null if none has yet.</param>
/// <param name="LastError">The last registration failure, or null when healthy; set for example while the virtual server is stopped.</param>
/// <param name="Healthy">Whether the subscription is currently delivering, that is LastError is null.</param>
public sealed record EventSubscriptionView(
    int VirtualServerId,
    IReadOnlyList<string> Categories,
    int ChannelId,
    int TextChannelId,
    DateTimeOffset Since,
    int SessionsOpened,
    int? ClientId,
    DateTimeOffset? LastEventAt,
    string? LastError,
    bool Healthy);

/// <summary>The result of subscribing.</summary>
/// <param name="Subscription">The subscription as it now stands.</param>
/// <param name="Cursor">Pass as 'after' to read only what arrives from now on.</param>
/// <param name="Note">How to continue.</param>
public sealed record EventSubscribeResult(EventSubscriptionView Subscription, long Cursor, string Note);

/// <summary>The result of unsubscribing.</summary>
/// <param name="Done">What happened, in words.</param>
/// <param name="Remaining">What is still subscribed on that virtual server, if anything.</param>
public sealed record EventUnsubscribeResult(string Done, EventSubscriptionView? Remaining);

/// <summary>An event as it arrived.</summary>
/// <param name="Sequence">Its cursor.</param>
/// <param name="ReceivedAt">When it was received.</param>
/// <param name="VirtualServerId">The virtual server it came from.</param>
/// <param name="Name">TeamSpeak's notification name, for example <c>notifytextmessage</c>.</param>
/// <param name="Categories">The categories it belongs to.</param>
/// <param name="Fields">Its fields, one set per record.</param>
public sealed record EventView(
    long Sequence,
    DateTimeOffset ReceivedAt,
    int VirtualServerId,
    string Name,
    IReadOnlyList<string> Categories,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Fields);

/// <summary>Events read from the buffer.</summary>
/// <param name="Events">The events, oldest first.</param>
/// <param name="NextCursor">Pass as 'after' on the next read.</param>
/// <param name="Missed">Events pushed out of the buffer before they were read.</param>
/// <param name="CursorReset">Whether reading started over because the cursor was too new.</param>
/// <param name="Subscriptions">What is currently subscribed on the profile.</param>
/// <param name="Notes">Anything the caller should know about gaps or missing subscriptions.</param>
public sealed record EventPollResult(
    IReadOnlyList<EventView> Events,
    long NextCursor,
    long Missed,
    bool CursorReset,
    IReadOnlyList<EventSubscriptionView> Subscriptions,
    IReadOnlyList<string> Notes);

/// <summary>Event subscriptions and the buffer.</summary>
/// <param name="Subscriptions">One entry per virtual server with a subscription.</param>
/// <param name="BufferCapacity">How many events are kept.</param>
/// <param name="Oldest">The cursor of the oldest event kept; 0 when empty.</param>
/// <param name="Newest">The cursor of the newest event; 0 before the first.</param>
public sealed record EventStatus(
    IReadOnlyList<EventSubscriptionView> Subscriptions,
    int BufferCapacity,
    long Oldest,
    long Newest);

/// <summary>An event category as the event tools accept it, lowercase in the tool schema.</summary>
/// <remarks>Mirrors <see cref="EventCategory"/> by member name; a test keeps the two in step.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter<EventCategoryName>))]
public enum EventCategoryName
{
    /// <summary><see cref="EventCategory.Server"/>.</summary>
    [JsonStringEnumMemberName("server")]
    Server,

    /// <summary><see cref="EventCategory.Channel"/>.</summary>
    [JsonStringEnumMemberName("channel")]
    Channel,

    /// <summary><see cref="EventCategory.TextServer"/>.</summary>
    [JsonStringEnumMemberName("textserver")]
    TextServer,

    /// <summary><see cref="EventCategory.TextChannel"/>.</summary>
    [JsonStringEnumMemberName("textchannel")]
    TextChannel,

    /// <summary><see cref="EventCategory.TextPrivate"/>.</summary>
    [JsonStringEnumMemberName("textprivate")]
    TextPrivate,

    /// <summary><see cref="EventCategory.Bans"/>.</summary>
    [JsonStringEnumMemberName("bans")]
    Bans,
}