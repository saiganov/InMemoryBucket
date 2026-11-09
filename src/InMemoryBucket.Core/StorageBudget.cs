using System.Globalization;

namespace InMemoryBucket.Core;

/// <summary>
/// Bytes held by the store: object data plus parts of in-progress multipart uploads. Reserving past the limit throws
/// <see cref="InvalidOperationException"/>, not an S3 error: a test that fills memory is a bug in the test, so it fails loudly.
/// </summary>
internal sealed class StorageBudget
{
    private long _used;

    public StorageBudget(long? limit)
    {
        if (limit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "MaxTotalBytes must be zero or more, or null for no limit.");
        }

        Limit = limit;
    }

    public long? Limit { get; }

    public long Used => Interlocked.Read(ref _used);

    /// <summary>Adds <paramref name="bytes"/> (negative to give some back) or throws if that would pass the limit; nothing changes on throw.</summary>
    public void Reserve(long bytes)
    {
        if (bytes <= 0 || Limit is not { } limit)
        {
            Interlocked.Add(ref _used, bytes);
            return;
        }

        while (true)
        {
            var current = Interlocked.Read(ref _used);
            var next = current + bytes;
            if (next > limit)
            {
                throw new InvalidOperationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"InMemoryBucket would hold {next} bytes, over MaxTotalBytes ({limit}). It holds {current} bytes now; this write adds {bytes}. Raise InMemoryBucketOptions.MaxTotalBytes or store less data in the test."));
            }

            if (Interlocked.CompareExchange(ref _used, next, current) == current)
            {
                return;
            }
        }
    }

    public void Release(long bytes) => Interlocked.Add(ref _used, -bytes);
}
