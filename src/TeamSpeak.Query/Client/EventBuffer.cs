using TeamSpeak.Query.Transport;

namespace TeamSpeak.Query.Client;

/// <summary>
/// A fixed-size, numbered buffer of received events that callers read from with a cursor.
/// </summary>
/// <remarks>
/// <para>
/// Every event gets the next sequence number. A caller keeps the last number it has seen and asks
/// for what came after it, so reading needs no state on this side. That is what lets events work
/// behind the stateless Streamable HTTP transport, where consecutive calls need not share a session.
/// </para>
/// <para>
/// When the buffer is full the oldest event makes room. A caller whose cursor has fallen behind is
/// told how many events it missed rather than silently skipping them.
/// </para>
/// </remarks>
public sealed class EventBuffer
{
    private readonly Lock _gate = new();
    private readonly BufferedEvent[] _ring;
    private int _start;
    private int _count;
    private long _next = 1;
    private TaskCompletionSource _arrival = NewArrival();

    /// <summary>Creates a buffer.</summary>
    /// <param name="capacity">How many events it keeps. At least one.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="capacity"/> is below one.</exception>
    public EventBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _ring = new BufferedEvent[capacity];
    }

    /// <summary>Gets how many events the buffer keeps.</summary>
    public int Capacity => _ring.Length;

    /// <summary>Gets the sequence number of the newest event, or 0 before the first.</summary>
    public long Newest
    {
        get
        {
            lock (_gate)
            {
                return _next - 1;
            }
        }
    }

    /// <summary>Gets the sequence number of the oldest event still kept, or 0 when empty.</summary>
    public long Oldest
    {
        get
        {
            lock (_gate)
            {
                return _count == 0 ? 0 : _ring[_start].Sequence;
            }
        }
    }

    /// <summary>Adds an event, dropping the oldest when the buffer is full.</summary>
    /// <param name="virtualServerId">The virtual server the event came from.</param>
    /// <param name="notification">The event.</param>
    /// <returns>The event as buffered, with its sequence number.</returns>
    public BufferedEvent Append(int virtualServerId, QueryEvent notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        TaskCompletionSource arrived;
        BufferedEvent buffered;

        lock (_gate)
        {
            buffered = new BufferedEvent(_next++, virtualServerId, notification);

            if (_count < _ring.Length)
            {
                _ring[(_start + _count) % _ring.Length] = buffered;
                _count++;
            }
            else
            {
                _ring[_start] = buffered;
                _start = (_start + 1) % _ring.Length;
            }

            arrived = _arrival;
            _arrival = NewArrival();
        }

        // Completed outside the lock, so a waiter's continuation never runs while holding it.
        arrived.TrySetResult();
        return buffered;
    }

    /// <summary>Reads the events after a cursor.</summary>
    /// <param name="after">The last sequence number the caller has seen; 0 for everything kept.</param>
    /// <param name="limit">The most events to return. At least one.</param>
    /// <param name="include">Which events to return; the cursor moves past the others too.</param>
    /// <returns>The events, the cursor to pass next, and how many events were missed.</returns>
    public EventPage Read(long after, int limit, Func<BufferedEvent, bool>? include = null) =>
        ReadCore(after, limit, include).Page;

    /// <summary>
    /// Reads the events after a cursor, waiting for one to arrive when there are none yet.
    /// </summary>
    /// <param name="after">The last sequence number the caller has seen.</param>
    /// <param name="limit">The most events to return.</param>
    /// <param name="timeout">How long to wait for a matching event.</param>
    /// <param name="include">Which events to return; the cursor moves past the others too.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>
    /// As soon as a matching event arrives or events were missed, the page; otherwise, after
    /// <paramref name="timeout"/>, an empty page whose cursor has moved past anything filtered out.
    /// </returns>
    public async Task<EventPage> WaitAsync(
        long after,
        int limit,
        TimeSpan timeout,
        Func<BufferedEvent, bool>? include = null,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        var cursor = after;

        while (true)
        {
            var (page, arrival) = ReadCore(cursor, limit, include);
            if (page.Events.Count > 0 || page.Missed > 0 || page.CursorReset)
            {
                return page;
            }

            // Keep the cursor moving past events the filter skipped, so they are not scanned again.
            cursor = page.NextCursor;

            try
            {
                await arrival.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return page;
            }
        }
    }

    private (EventPage Page, Task Arrival) ReadCore(long after, int limit, Func<BufferedEvent, bool>? include)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        lock (_gate)
        {
            var newest = _next - 1;

            // A cursor from beyond the newest event belongs to an earlier buffer, for example before
            // the server restarted. Starting over is the only way the caller sees anything again.
            var reset = after > newest;
            var from = reset ? 0 : Math.Max(0, after);

            var oldest = _count == 0 ? _next : _ring[_start].Sequence;
            var missed = from + 1 < oldest && from < newest ? oldest - (from + 1) : 0;

            var events = new List<BufferedEvent>();
            var cursor = Math.Max(from, oldest - 1);

            for (var i = 0; i < _count; i++)
            {
                var buffered = _ring[(_start + i) % _ring.Length];
                if (buffered.Sequence <= from)
                {
                    continue;
                }

                if (include is null || include(buffered))
                {
                    if (events.Count == limit)
                    {
                        break;
                    }

                    events.Add(buffered);
                }

                cursor = buffered.Sequence;
            }

            return (new EventPage(events, cursor, missed, newest, reset), _arrival.Task);
        }
    }

    private static TaskCompletionSource NewArrival() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>An event with its place in the buffer.</summary>
/// <param name="Sequence">Its number; later events have higher numbers.</param>
/// <param name="VirtualServerId">The virtual server it came from.</param>
/// <param name="Event">The event.</param>
public sealed record BufferedEvent(long Sequence, int VirtualServerId, QueryEvent Event);

/// <summary>What one read of an <see cref="EventBuffer"/> returned.</summary>
/// <param name="Events">The events, oldest first.</param>
/// <param name="NextCursor">The cursor to pass on the next read.</param>
/// <param name="Missed">How many events after the given cursor were already dropped to make room.</param>
/// <param name="Newest">The newest sequence number in the buffer.</param>
/// <param name="CursorReset">Whether the cursor was beyond the newest event and reading started over.</param>
public sealed record EventPage(
    IReadOnlyList<BufferedEvent> Events,
    long NextCursor,
    long Missed,
    long Newest,
    bool CursorReset);