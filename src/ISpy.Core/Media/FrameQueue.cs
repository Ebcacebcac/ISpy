namespace ISpy.Core.Media;

/// <summary>
/// Hand-off buffer between a decoder thread and the renderer.
/// </summary>
/// <remarks>
/// Deliberately tiny and lossy. For live video the newest frame is the only one worth drawing, so
/// when the renderer falls behind we drop the oldest rather than queueing: an unbounded queue on a
/// 16-camera grid turns a momentary stall into ever-growing latency and memory, which is exactly
/// the "video drifts minutes behind" failure people see in other clients.
/// Dropped items are handed to <paramref name="release"/> so GPU frames get returned to their pool.
/// </remarks>
public sealed class FrameQueue<T>(int capacity = 2, Action<T>? release = null)
{
    private readonly Queue<T> _items = new(capacity);
    private readonly Lock _gate = new();

    public int Capacity { get; } = capacity > 0
        ? capacity
        : throw new ArgumentOutOfRangeException(nameof(capacity));

    /// <summary>Frames discarded because the renderer could not keep up. A health signal, not an error.</summary>
    public long DroppedCount { get; private set; }

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    /// <summary>Adds a frame, dropping the oldest if full. Returns false when something was dropped.</summary>
    public bool Enqueue(T item)
    {
        T? dropped = default;
        bool didDrop;

        lock (_gate)
        {
            didDrop = _items.Count >= Capacity;
            if (didDrop)
            {
                dropped = _items.Dequeue();
                DroppedCount++;
            }

            _items.Enqueue(item);
        }

        // Released outside the lock: a GPU release can block, and holding the lock would stall the
        // decoder thread behind the renderer.
        if (didDrop && dropped is not null) release?.Invoke(dropped);

        return !didDrop;
    }

    public bool TryDequeue(out T item)
    {
        lock (_gate)
        {
            if (_items.Count > 0)
            {
                item = _items.Dequeue();
                return true;
            }
        }

        item = default!;
        return false;
    }

    /// <summary>
    /// Takes the newest frame and discards everything older. What the renderer calls when it is
    /// ready to draw: presenting a stale frame it already skipped past helps nobody.
    /// </summary>
    public bool TryDequeueLatest(out T item)
    {
        var stale = new List<T>();

        lock (_gate)
        {
            if (_items.Count == 0)
            {
                item = default!;
                return false;
            }

            while (_items.Count > 1)
            {
                stale.Add(_items.Dequeue());
                DroppedCount++;
            }

            item = _items.Dequeue();
        }

        foreach (var value in stale) release?.Invoke(value);
        return true;
    }

    public void Clear()
    {
        List<T> pending;

        lock (_gate)
        {
            pending = [.. _items];
            _items.Clear();
        }

        foreach (var value in pending) release?.Invoke(value);
    }
}
