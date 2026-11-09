using System.Collections.Concurrent;

namespace InMemoryBucket.Core;

/// <summary>
/// The root of the in-memory store: bucket name to bucket. Knows nothing about HTTP or XML.
/// </summary>
public sealed class ObjectStore
{
    private readonly TimeProvider _clock;
    private readonly Func<string> _uploadIds;
    private readonly long? _maxTotalBytes;

    // Buckets and the bytes they hold, swapped as one on Clear: a request still holding an old bucket settles with the old
    // budget, and a CreateBucket racing Clear lands in the discarded dictionary instead of a live bucket on a dead budget.
    private volatile Generation _current;

    internal ObjectStore(TimeProvider? clock = null, Func<string>? uploadIds = null, long? maxTotalBytes = null)
    {
        _clock = clock ?? TimeProvider.System;
        _uploadIds = uploadIds ?? (() => Guid.NewGuid().ToString("N"));
        _maxTotalBytes = maxTotalBytes;
        _current = new Generation(new StorageBudget(maxTotalBytes));
    }

    /// <summary>Bytes held right now: the data of every object plus the parts of in-progress multipart uploads.</summary>
    public long TotalBytes => _current.Budget.Used;

    /// <summary>Buckets in name order, as ListBuckets returns them.</summary>
    public IReadOnlyList<Bucket> Buckets => _current.Buckets.Values.OrderBy(b => b.Name, StringComparer.Ordinal).ToArray();

    /// <summary>Creates the bucket. Returns false when it already exists; the protocol layer decides whether that is 200 or 409.</summary>
    internal bool TryCreateBucket(string name, out Bucket bucket)
    {
        BucketNames.EnsureValid(name);
        var generation = _current;
        var created = new Bucket(name, _clock.GetUtcNow(), _clock, _uploadIds, generation.Budget);
        bucket = generation.Buckets.GetOrAdd(name, created);
        return ReferenceEquals(bucket, created);
    }

    /// <summary>Looks up a bucket by name.</summary>
    /// <param name="name">The bucket name.</param>
    /// <param name="bucket">The bucket when found; otherwise null.</param>
    /// <returns>True when the bucket exists.</returns>
    public bool TryGetBucket(string name, out Bucket bucket) => _current.Buckets.TryGetValue(name, out bucket!);

    /// <summary>Gets a bucket by name.</summary>
    /// <param name="name">The bucket name.</param>
    /// <returns>The bucket.</returns>
    /// <exception cref="S3Exception">The bucket does not exist (<see cref="S3ErrorCode.NoSuchBucket"/>).</exception>
    public Bucket GetBucket(string name)
        => TryGetBucket(name, out var bucket) ? bucket : throw new S3Exception(S3ErrorCode.NoSuchBucket, resource: "/" + name);

    internal void DeleteBucket(string name)
    {
        var generation = _current;
        if (!generation.Buckets.TryGetValue(name, out var bucket))
        {
            throw new S3Exception(S3ErrorCode.NoSuchBucket, resource: "/" + name);
        }

        if (!bucket.TryMarkDeleted())
        {
            throw new S3Exception(S3ErrorCode.BucketNotEmpty, resource: "/" + name);
        }

        generation.Buckets.TryRemove(KeyValuePair.Create(name, bucket));
        bucket.AbortAllUploads(); // S3 drops a deleted bucket's in-progress uploads; their parts no longer count
    }

    internal void Clear() => _current = new Generation(new StorageBudget(_maxTotalBytes));

    private sealed class Generation(StorageBudget budget)
    {
        public ConcurrentDictionary<string, Bucket> Buckets { get; } = new(StringComparer.Ordinal);

        public StorageBudget Budget { get; } = budget;
    }
}
