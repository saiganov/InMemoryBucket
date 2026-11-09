using System.Collections;

namespace InMemoryBucket.Hooks;

/// <summary>
/// A bounded, thread-safe journal of every request the server handled, oldest first. Enumeration works on a snapshot,
/// so a test can iterate while the application under test keeps sending requests.
/// </summary>
public sealed class RequestLog : IReadOnlyCollection<RecordedRequest>
{
    private readonly object _gate = new();
    private readonly Queue<RecordedRequest> _entries = new();
    private readonly int _capacity;

    internal RequestLog(int capacity)
    {
        _capacity = Math.Max(0, capacity);
    }

    /// <summary>The number of requests the log holds now, at most <see cref="InMemoryBucketOptions.RequestLogCapacity"/>.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>The most recent request, or null when nothing was recorded.</summary>
    public RecordedRequest? Last
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count == 0 ? null : _entries.Last();
            }
        }
    }

    /// <summary>Removes every entry. <see cref="InMemoryBucketServer.Reset"/> does this too.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    /// <summary>Enumerates a snapshot of the log, oldest first.</summary>
    /// <returns>An enumerator over the snapshot.</returns>
    public IEnumerator<RecordedRequest> GetEnumerator()
    {
        RecordedRequest[] snapshot;
        lock (_gate)
        {
            snapshot = _entries.ToArray();
        }

        return ((IEnumerable<RecordedRequest>)snapshot).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal void Add(RecordedRequest entry)
    {
        if (_capacity == 0)
        {
            return;
        }

        lock (_gate)
        {
            while (_entries.Count >= _capacity)
            {
                _entries.Dequeue();
            }

            _entries.Enqueue(entry);
        }
    }
}
