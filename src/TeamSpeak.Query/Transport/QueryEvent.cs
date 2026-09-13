namespace TeamSpeak.Query.Transport;

/// <summary>
/// An event pushed by the server after a <c>servernotifyregister</c> subscription.
/// </summary>
/// <param name="Name">The notification name, for example <c>notifycliententerview</c>.</param>
/// <param name="Records">The payload records carried by the notification.</param>
/// <param name="ReceivedAt">The moment the event was read from the connection.</param>
public sealed record QueryEvent(
    string Name,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Records,
    DateTimeOffset ReceivedAt);