using System.Text.Json.Serialization;

using Microsoft.Extensions.Hosting;

using ModelContextProtocol.Server;

using TeamSpeak.Query.Client;

namespace TeamSpeak.Mcp.Hosting;

/// <summary>
/// Reads each profile's event buffer and pushes what arrives into the session as a channel
/// notification.
/// </summary>
/// <remarks>
/// It starts at the newest event rather than replaying what a subscription collected earlier, since
/// the point is what is happening now. Nothing is pushed until something subscribes with
/// <c>ts_events_subscribe</c>: the buffer stays empty until then.
/// </remarks>
public sealed class ChannelPushService(
    ChannelSession session,
    ChannelPush push,
    QueryEventHub hub,
    ProfileRegistry profiles) : BackgroundService
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    /// <summary>Pushes events for as long as the host runs.</summary>
    /// <param name="stoppingToken">Stops the push.</param>
    /// <returns>A task that completes when the host stops.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var server = await session.OpenedAsync(stoppingToken).ConfigureAwait(false);

        await Task.WhenAll(profiles.Names.Select(name => PushAsync(server, name, stoppingToken)))
            .ConfigureAwait(false);
    }

    private async Task PushAsync(McpServer server, string profile, CancellationToken cancellationToken)
    {
        var buffer = hub.BufferFor(profile);
        var cursor = buffer.Newest;

        while (!cancellationToken.IsCancellationRequested)
        {
            var page = await buffer.WaitAsync(cursor, 50, Wait, null, cancellationToken).ConfigureAwait(false);
            cursor = page.NextCursor;

            foreach (var buffered in page.Events.Where(buffered => push.MayPush(buffered.Event)))
            {
                await server.SendNotificationAsync(
                    ChannelPush.Method,
                    new ChannelNotification(ChannelPush.Content(buffered), ChannelPush.Meta(profile, buffered)),
                    ResultJson.Options,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>The two parameters a channel notification carries.</summary>
/// <param name="Content">The body of the channel tag.</param>
/// <param name="Meta">Attributes on the tag; every key an identifier.</param>
public sealed record ChannelNotification(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("meta")] IReadOnlyDictionary<string, string> Meta);