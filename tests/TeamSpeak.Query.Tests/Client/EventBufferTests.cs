using TeamSpeak.Query.Client;
using TeamSpeak.Query.Transport;

namespace TeamSpeak.Query.Tests.Client;

public class EventBufferTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static QueryEvent Event(string name) => new(name, [], DateTimeOffset.UtcNow);

    [Fact]
    public void Numbers_events_and_reads_those_after_the_cursor()
    {
        var buffer = new EventBuffer(10);
        buffer.Append(1, Event("notifyone"));
        buffer.Append(1, Event("notifytwo"));
        buffer.Append(1, Event("notifythree"));

        var page = buffer.Read(after: 1, limit: 10);

        Assert.Equal(["notifytwo", "notifythree"], page.Events.Select(e => e.Event.Name));
        Assert.Equal([2L, 3L], page.Events.Select(e => e.Sequence));
        Assert.Equal((3L, 3L, 0L, false), (page.NextCursor, page.Newest, page.Missed, page.CursorReset));
    }

    [Fact]
    public void A_full_buffer_drops_the_oldest_and_says_how_many_a_late_reader_missed()
    {
        var buffer = new EventBuffer(3);
        for (var i = 1; i <= 5; i++)
        {
            buffer.Append(1, Event($"notify{i}"));
        }

        var page = buffer.Read(after: 0, limit: 10);

        Assert.Equal([3L, 4L, 5L], page.Events.Select(e => e.Sequence));
        Assert.Equal(2, page.Missed);
        Assert.Equal((3L, 5L), (buffer.Oldest, buffer.Newest));
    }

    [Fact]
    public void A_limit_stops_the_cursor_at_the_last_event_returned()
    {
        var buffer = new EventBuffer(10);
        for (var i = 1; i <= 5; i++)
        {
            buffer.Append(1, Event($"notify{i}"));
        }

        var first = buffer.Read(after: 0, limit: 2);
        var second = buffer.Read(first.NextCursor, limit: 10);

        Assert.Equal([1L, 2L], first.Events.Select(e => e.Sequence));
        Assert.Equal(2, first.NextCursor);
        Assert.Equal([3L, 4L, 5L], second.Events.Select(e => e.Sequence));
    }

    [Fact]
    public void Filtered_out_events_still_move_the_cursor()
    {
        var buffer = new EventBuffer(10);
        buffer.Append(1, Event("notifytextmessage"));
        buffer.Append(1, Event("notifyclientmoved"));
        buffer.Append(1, Event("notifyclientmoved"));

        var page = buffer.Read(after: 0, limit: 10, include: e => e.Event.Name == "notifytextmessage");

        Assert.Single(page.Events);
        Assert.Equal(3, page.NextCursor);
    }

    [Fact]
    public void A_cursor_beyond_the_newest_event_starts_over_and_says_so()
    {
        var buffer = new EventBuffer(10);
        buffer.Append(1, Event("notifyone"));

        var page = buffer.Read(after: 500, limit: 10);

        Assert.True(page.CursorReset);
        Assert.Single(page.Events);
    }

    [Fact]
    public void An_empty_buffer_returns_nothing_and_keeps_the_cursor()
    {
        var page = new EventBuffer(10).Read(after: 0, limit: 10);

        Assert.Empty(page.Events);
        Assert.Equal((0L, 0L, 0L), (page.NextCursor, page.Missed, page.Newest));
    }

    [Fact]
    public async Task Waiting_returns_as_soon_as_a_matching_event_arrives()
    {
        var buffer = new EventBuffer(10);
        buffer.Append(1, Event("notifyclientmoved"));

        var waiting = buffer.WaitAsync(after: 0, limit: 10, TimeSpan.FromSeconds(10), e => e.Event.Name == "notifytextmessage", Ct);
        await Task.Delay(50, Ct);
        Assert.False(waiting.IsCompleted);

        buffer.Append(2, Event("notifytextmessage"));
        var page = await waiting.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        var arrived = Assert.Single(page.Events);
        Assert.Equal((2L, 2), (arrived.Sequence, arrived.VirtualServerId));
    }

    [Fact]
    public async Task Waiting_gives_up_after_the_timeout_with_the_cursor_moved_past_skipped_events()
    {
        var buffer = new EventBuffer(10);
        buffer.Append(1, Event("notifyclientmoved"));

        var page = await buffer.WaitAsync(after: 0, limit: 10, TimeSpan.FromMilliseconds(100), e => e.Event.Name == "notifytextmessage", Ct);

        Assert.Empty(page.Events);
        Assert.Equal(1, page.NextCursor);
    }

    [Fact]
    public void Refuses_a_capacity_below_one() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new EventBuffer(0));
}