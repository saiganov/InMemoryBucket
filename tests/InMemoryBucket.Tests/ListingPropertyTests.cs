using InMemoryBucket.Core;
using CsCheck;
using Microsoft.Extensions.Time.Testing;

namespace InMemoryBucket.Tests;

/// <summary>
/// Paging through a listing with any prefix, delimiter and page size must yield exactly what a one-shot LINQ model yields:
/// every key or common prefix once, in S3 order, and the walk must end. Keys use a tiny alphabet so they share prefixes often.
/// </summary>
public class ListingPropertyTests
{
    private const int Iterations = 2_000;

    private static readonly Gen<string> Key = Gen.String[Gen.Char["ab/-"], 1, 5];

    private static readonly Gen<(string[] Keys, string Prefix, string? Delimiter, int PageSize)> Listing =
        Gen.Select(
            Key.Array[0, 12],
            Gen.OneOfConst("", "a", "a/", "b-"),
            Gen.OneOfConst<string?>(null, "/", "-", "ab"),
            Gen.Int[1, 5],
            (keys, prefix, delimiter, pageSize) => (keys.Distinct(StringComparer.Ordinal).ToArray(), prefix, delimiter, pageSize));

    [Fact]
    public void ListObjectsV2_paging_matches_the_model()
    {
        Listing.Sample(t =>
        {
            var bucket = NewBucket(t.Keys);
            var walked = new List<string>();
            string? token = null;
            var pages = 0;
            ListObjectsV2Result page;
            do
            {
                page = bucket.ListObjectsV2(new ListObjectsV2Query(t.Prefix, t.Delimiter, t.PageSize, ContinuationToken: token));
                Assert.True(page.KeyCount <= t.PageSize);
                Assert.Equal(page.KeyCount, page.Contents.Count + page.CommonPrefixes.Count);
                walked.AddRange(Merge(page.Contents.Select(o => o.Key), page.CommonPrefixes));
                token = page.NextContinuationToken;
                Assert.True(++pages <= t.Keys.Length + 1, "listing did not terminate");
            }
            while (page.IsTruncated);

            Assert.Equal(Model(t.Keys, t.Prefix, t.Delimiter), walked);
        }, iter: Iterations);
    }

    [Fact]
    public void ListMultipartUploads_paging_matches_the_model()
    {
        Listing.Sample(t =>
        {
            var bucket = NewBucket();
            foreach (var key in t.Keys)
            {
                bucket.CreateMultipartUpload(key, ObjectAttributes.Default, null, null);
            }

            var walked = new List<string>();
            string? keyMarker = null;
            string? uploadIdMarker = null;
            var pages = 0;
            ListMultipartUploadsResult page;
            do
            {
                page = bucket.ListMultipartUploads(new ListMultipartUploadsQuery(t.Prefix, t.Delimiter, t.PageSize, keyMarker, uploadIdMarker));
                Assert.True(page.Uploads.Count + page.CommonPrefixes.Count <= t.PageSize);
                walked.AddRange(Merge(page.Uploads.Select(u => u.Key), page.CommonPrefixes));
                keyMarker = page.NextKeyMarker;
                uploadIdMarker = page.NextUploadIdMarker;
                Assert.True(++pages <= t.Keys.Length + 1, "listing did not terminate");
            }
            while (page.IsTruncated);

            Assert.Equal(Model(t.Keys, t.Prefix, t.Delimiter), walked);
        }, iter: Iterations);
    }

    /// <summary>
    /// S3 semantics in one pass: keys under the prefix in byte order, each rolled up to prefix + text + delimiter when the
    /// delimiter occurs after the prefix, duplicates of a common prefix dropped. A common prefix's keys are contiguous in
    /// that order, so first occurrence is also its listing position.
    /// </summary>
    private static List<string> Model(string[] keys, string prefix, string? delimiter)
        => keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k =>
            {
                if (string.IsNullOrEmpty(delimiter))
                {
                    return k;
                }

                var i = k.IndexOf(delimiter, prefix.Length, StringComparison.Ordinal);
                return i < 0 ? k : k[..(i + delimiter.Length)];
            })
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>A page reports keys and common prefixes separately; S3 order interleaves them.</summary>
    private static IEnumerable<string> Merge(IEnumerable<string> keys, IEnumerable<string> prefixes)
        => keys.Concat(prefixes).OrderBy(k => k, StringComparer.Ordinal);

    private static Bucket NewBucket(params string[] keys)
    {
        var store = new ObjectStore(new FakeTimeProvider(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero)));
        store.TryCreateBucket("test", out var bucket);
        foreach (var key in keys)
        {
            bucket.Put(key, new byte[1], ObjectAttributes.Default, ChecksumSet.Empty);
        }

        return bucket;
    }
}
