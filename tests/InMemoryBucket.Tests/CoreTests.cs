using System.Text;
using InMemoryBucket.Core;
using Microsoft.Extensions.Time.Testing;

namespace InMemoryBucket.Tests;

public class CoreTests
{
    private static Bucket NewBucket(params string[] keys)
    {
        var store = new ObjectStore(new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero)));
        store.TryCreateBucket("test", out var bucket);
        foreach (var key in keys)
        {
            bucket.Put(key, Encoding.UTF8.GetBytes(key), ObjectAttributes.Default, ChecksumSet.Empty);
        }

        return bucket;
    }

    [Fact]
    public void A_deleted_bucket_takes_no_more_writes()
    {
        var store = new ObjectStore(new FakeTimeProvider());
        store.TryCreateBucket("gone", out var bucket);

        store.DeleteBucket("gone");

        var put = Assert.Throws<S3Exception>(() => bucket.Put("k", Encoding.UTF8.GetBytes("x"), ObjectAttributes.Default, ChecksumSet.Empty));
        Assert.Equal(S3ErrorCode.NoSuchBucket, put.Code);
        var upload = Assert.Throws<S3Exception>(() => bucket.CreateMultipartUpload("k", ObjectAttributes.Default, null, null));
        Assert.Equal(S3ErrorCode.NoSuchBucket, upload.Code);

        // A bucket created again under the same name is a new, empty one.
        store.TryCreateBucket("gone", out var again);
        Assert.NotSame(bucket, again);
        Assert.Equal(0, again.ObjectCount);
    }

    [Fact]
    public async Task A_write_racing_DeleteBucket_is_never_accepted_and_lost()
    {
        var store = new ObjectStore(new FakeTimeProvider());
        for (var i = 0; i < 2000; i++)
        {
            store.TryCreateBucket("race", out var bucket);
            using var start = new Barrier(2);

            var put = Task.Run(() =>
            {
                start.SignalAndWait();
                try
                {
                    bucket.Put("k", Encoding.UTF8.GetBytes("x"), ObjectAttributes.Default, ChecksumSet.Empty);
                    return true;
                }
                catch (S3Exception e) when (e.Code == S3ErrorCode.NoSuchBucket)
                {
                    return false;
                }
            });
            var delete = Task.Run(() =>
            {
                start.SignalAndWait();
                try
                {
                    store.DeleteBucket("race");
                    return true;
                }
                catch (S3Exception e) when (e.Code == S3ErrorCode.BucketNotEmpty)
                {
                    return false;
                }
            });

            var (written, deleted) = (await put, await delete);

            // Exactly one wins: an accepted write keeps the bucket, a successful delete rejects the write.
            Assert.True(written != deleted, $"iteration {i}: written={written}, deleted={deleted}");
            if (written)
            {
                Assert.True(store.GetBucket("race").TryGet("k", out _));
                store.GetBucket("race").Delete("k");
                store.DeleteBucket("race");
            }
        }
    }

    [Fact]
    public void Metadata_limit_is_2048_bytes_of_names_and_values()
    {
        var bucket = NewBucket();
        ObjectAttributes WithMetadata(int valueBytes) => ObjectAttributes.Default with
        {
            UserMetadata = ObjectAttributes.Default.UserMetadata.Add("abcd", new string('v', valueBytes)),
        };

        bucket.Put("fits", Encoding.UTF8.GetBytes("x"), WithMetadata(2044), ChecksumSet.Empty);
        var error = Assert.Throws<S3Exception>(() => bucket.Put("over", Encoding.UTF8.GetBytes("x"), WithMetadata(2045), ChecksumSet.Empty));

        Assert.Equal(S3ErrorCode.MetadataTooLarge, error.Code);
        Assert.False(bucket.TryGet("over", out _));
    }

    [Theory]
    [InlineData("k=v%0A")]
    [InlineData("k%0A=v")]
    [InlineData("k=v%09")]
    public void Tag_with_a_control_character_is_InvalidTag(string header)
    {
        var error = Assert.Throws<S3Exception>(() => ObjectTags.ParseHeader(header));

        Assert.Equal(S3ErrorCode.InvalidTag, error.Code);
    }

    [Fact]
    public void ETag_is_quoted_md5_like_s3()
    {
        var bucket = NewBucket();
        var obj = bucket.Put("k", Encoding.UTF8.GetBytes("hello"), ObjectAttributes.Default, ChecksumSet.Empty);

        Assert.Equal("\"5d41402abc4b2a76b9719d911017c592\"", obj.ETag);
    }

    [Fact]
    public void Listing_rolls_keys_up_into_common_prefixes()
    {
        var bucket = NewBucket("a/1", "a/2", "b/1", "c");

        var result = bucket.ListObjectsV2(new ListObjectsV2Query(Delimiter: "/"));

        Assert.Equal(["c"], result.Contents.Select(o => o.Key));
        Assert.Equal(["a/", "b/"], result.CommonPrefixes);
        Assert.Equal(3, result.KeyCount);
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public void Listing_paginates_with_continuation_tokens()
    {
        var bucket = NewBucket("k1", "k2", "k3", "k4", "k5");

        var page1 = bucket.ListObjectsV2(new ListObjectsV2Query(MaxKeys: 2));
        var page2 = bucket.ListObjectsV2(new ListObjectsV2Query(MaxKeys: 2, ContinuationToken: page1.NextContinuationToken));
        var page3 = bucket.ListObjectsV2(new ListObjectsV2Query(MaxKeys: 2, ContinuationToken: page2.NextContinuationToken));

        Assert.True(page1.IsTruncated);
        Assert.Equal(["k1", "k2"], page1.Contents.Select(o => o.Key));
        Assert.Equal(["k3", "k4"], page2.Contents.Select(o => o.Key));
        Assert.Equal(["k5"], page3.Contents.Select(o => o.Key));
        Assert.False(page3.IsTruncated);
        Assert.Null(page3.NextContinuationToken);
    }

    [Fact]
    public void Listing_with_prefix_and_delimiter_only_descends_one_level()
    {
        var bucket = NewBucket("photos/2026/a.png", "photos/2026/b.png", "photos/2025/c.png", "photos/readme.txt", "other");

        var result = bucket.ListObjectsV2(new ListObjectsV2Query(Prefix: "photos/", Delimiter: "/"));

        Assert.Equal(["photos/readme.txt"], result.Contents.Select(o => o.Key));
        Assert.Equal(["photos/2025/", "photos/2026/"], result.CommonPrefixes);
    }

    [Fact]
    public void Deleting_a_non_empty_bucket_fails_like_s3()
    {
        var store = new ObjectStore();
        store.TryCreateBucket("bucket-b", out var bucket);
        bucket.Put("k", new byte[1], ObjectAttributes.Default, ChecksumSet.Empty);

        var error = Assert.Throws<S3Exception>(() => store.DeleteBucket("bucket-b"));

        Assert.Equal(S3ErrorCode.BucketNotEmpty, error.Code);
    }

    [Fact]
    public async Task Concurrent_puts_to_one_bucket_lose_nothing()
    {
        var bucket = NewBucket();

        await Parallel.ForAsync(0, 1000, (i, _) =>
        {
            bucket.Put($"key-{i:D4}", BitConverter.GetBytes(i), ObjectAttributes.Default, ChecksumSet.Empty);
            return ValueTask.CompletedTask;
        });

        Assert.Equal(1000, bucket.ObjectCount);
        Assert.Equal(1000, bucket.ListObjectsV2(new ListObjectsV2Query()).KeyCount);
    }

    [Theory]
    [InlineData("my-bucket", true)]
    [InlineData("my.bucket.name", true)]
    [InlineData("ab", false)]
    [InlineData("MyBucket", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("bad..dots", false)]
    [InlineData("-leading", false)]
    public void Validates_bucket_names(string name, bool valid)
    {
        Assert.Equal(valid, BucketNames.IsValid(name));
    }

    [Fact]
    public void Crc32c_matches_known_vector()
    {
        // CRC-32C("123456789") = 0xE3069283
        var raw = Checksums.ComputeRaw(ChecksumAlgorithm.Crc32C, Encoding.ASCII.GetBytes("123456789"));

        Assert.Equal(new byte[] { 0xE3, 0x06, 0x92, 0x83 }, raw);
    }

    [Fact]
    public void Crc32_matches_known_vector()
    {
        // CRC-32("123456789") = 0xCBF43926
        var raw = Checksums.ComputeRaw(ChecksumAlgorithm.Crc32, Encoding.ASCII.GetBytes("123456789"));

        Assert.Equal(new byte[] { 0xCB, 0xF4, 0x39, 0x26 }, raw);
    }
}
