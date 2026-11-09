using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using InMemoryBucket.AwsSdk;
using InMemoryBucket.Core;
using Microsoft.Extensions.Time.Testing;

namespace InMemoryBucket.Tests;

/// <summary><see cref="InMemoryBucketOptions.MaxTotalBytes"/> and <see cref="InMemoryBucketOptions.UploadIdGenerator"/>.</summary>
public sealed class StorageLimitAndDeterminismTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_write_past_MaxTotalBytes_throws_from_SendAsync_with_size_and_limit()
    {
        using var server = new InMemoryBucketServer(o =>
        {
            o.MaxTotalBytes = 10;
            o.CreateBuckets("limited");
        });
        using var http = server.CreateHttpClient();

        using var ok = await http.PutAsync("limited/a", new ByteArrayContent(new byte[6]), Ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => http.PutAsync("limited/b", new ByteArrayContent(new byte[5]), Ct));
        Assert.Contains("11", error.Message);
        Assert.Contains("MaxTotalBytes (10)", error.Message);
        Assert.Contains("holds 6", error.Message);

        Assert.Equal(["a"], server.ListKeys("limited"));
        Assert.Equal(6, server.Store.TotalBytes);
    }

    [Fact]
    public async Task The_sdk_surfaces_the_limit_as_the_same_exception()
    {
        using var server = new InMemoryBucketServer(o =>
        {
            o.MaxTotalBytes = 4;
            o.CreateBuckets("limited");
        });
        using var s3 = server.CreateClient();

        var error = await Assert.ThrowsAnyAsync<Exception>(() => s3.PutObjectAsync(new PutObjectRequest { BucketName = "limited", Key = "k", ContentBody = "too long" }, Ct));

        Assert.IsType<InvalidOperationException>(error is Amazon.Runtime.AmazonClientException { InnerException: { } inner } ? inner : error);
    }

    [Fact]
    public void Overwrites_count_the_difference_and_deletes_give_bytes_back()
    {
        using var server = new InMemoryBucketServer(o =>
        {
            o.MaxTotalBytes = 10;
            o.CreateBuckets("limited");
        });

        server.Seed("limited", "k", new byte[8]);
        server.Seed("limited", "k", new byte[10]); // replaces 8 bytes: 10 in total, not 18
        Assert.Equal(10, server.Store.TotalBytes);

        Assert.Throws<InvalidOperationException>(() => server.Seed("limited", "other", new byte[1]));

        server.Reset();
        Assert.Equal(0, server.Store.TotalBytes);
        server.Seed("limited", "other", new byte[10]);
    }

    [Fact]
    public async Task Multipart_parts_count_until_the_upload_completes_or_is_aborted()
    {
        const int part = 5 * 1024 * 1024;
        using var server = new InMemoryBucketServer(o =>
        {
            o.MaxTotalBytes = 3L * part;
            o.CreateBuckets("limited");
        });
        using var s3 = server.CreateClient();

        var aborted = await s3.InitiateMultipartUploadAsync("limited", "aborted", Ct);
        await UploadPart(s3, "aborted", aborted.UploadId, 1, part);
        Assert.Equal(part, server.Store.TotalBytes);
        await s3.AbortMultipartUploadAsync("limited", "aborted", aborted.UploadId, Ct);
        Assert.Equal(0, server.Store.TotalBytes);

        var upload = await s3.InitiateMultipartUploadAsync("limited", "big", Ct);
        var first = await UploadPart(s3, "big", upload.UploadId, 1, part);
        var replaced = await UploadPart(s3, "big", upload.UploadId, 1, part); // same part number again: still one part
        Assert.Equal(part, server.Store.TotalBytes);
        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = "limited",
            Key = "big",
            UploadId = upload.UploadId,
            PartETags = [new PartETag(1, replaced.ETag)],
        }, Ct);
        Assert.NotNull(first.ETag);
        Assert.Equal(part, server.Store.TotalBytes); // the object; its part is released

        var tooBig = await s3.InitiateMultipartUploadAsync("limited", "too-big", Ct);
        await UploadPart(s3, "too-big", tooBig.UploadId, 1, part);
        await Assert.ThrowsAnyAsync<Exception>(() => UploadPart(s3, "too-big", tooBig.UploadId, 2, part + 1)); // 3 parts + 1 byte
        Assert.Equal(2L * part, server.Store.TotalBytes);
    }

    [Fact]
    public async Task Deleting_a_bucket_releases_its_in_progress_uploads()
    {
        using var server = new InMemoryBucketServer(o => o.CreateBuckets("doomed"));
        using var s3 = server.CreateClient();
        var upload = await s3.InitiateMultipartUploadAsync("doomed", "k", Ct);
        await UploadPart(s3, "k", upload.UploadId, 1, 100, bucket: "doomed");

        await s3.DeleteBucketAsync("doomed", Ct);

        Assert.Equal(0, server.Store.TotalBytes);
    }

    [Fact]
    public async Task Two_runs_with_a_fake_clock_and_deterministic_ids_give_identical_responses()
    {
        Assert.Equal(await Transcript(), await Transcript());

        static async Task<string> Transcript()
        {
            var next = 0;
            using var server = new InMemoryBucketServer(o =>
            {
                o.TimeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
                o.UploadIdGenerator = () => $"upload-{++next}";
                o.CreateBuckets("snap");
            });
            using var http = server.CreateHttpClient();
            var log = new StringBuilder();
            foreach (var (method, path, body) in new[]
            {
                ("PUT", "snap/a.txt", "hello"),
                ("GET", "snap/a.txt", null),
                ("POST", "snap/big?uploads", null),
                ("GET", "snap?uploads", null),
                ("GET", "snap?list-type=2", null),
                ("GET", "snap/missing", null),
            })
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = body is null ? null : new StringContent(body) };
                using var response = await http.SendAsync(request, Ct);
                log.AppendLine($"{method} {path} -> {(int)response.StatusCode}");
                foreach (var (name, values) in response.Headers.Concat(response.Content.Headers).OrderBy(h => h.Key, StringComparer.Ordinal))
                {
                    log.AppendLine($"{name}: {string.Join(",", values)}");
                }

                log.AppendLine(await response.Content.ReadAsStringAsync(Ct));
            }

            Assert.Contains("upload-1", log.ToString());
            return log.ToString();
        }
    }

    [Fact]
    public async Task Concurrent_part_uploads_and_aborts_never_drift_the_count()
    {
        using var server = new InMemoryBucketServer(o => o.CreateBuckets("race"));
        using var http = server.CreateHttpClient();
        for (var i = 0; i < 300; i++)
        {
            using var created = await http.PostAsync("race/k?uploads", null, Ct);
            var body = await created.Content.ReadAsStringAsync(Ct);
            var uploadId = body[(body.IndexOf("<UploadId>", StringComparison.Ordinal) + 10)..body.IndexOf("</UploadId>", StringComparison.Ordinal)];
            var path = $"race/k?partNumber=1&uploadId={uploadId}";
            await Task.WhenAll(
                Task.Run(() => http.PutAsync(path, new ByteArrayContent(new byte[10]), Ct), Ct),
                Task.Run(() => http.PutAsync(path, new ByteArrayContent(new byte[10]), Ct), Ct),
                Task.Run(() => http.DeleteAsync($"race/k?uploadId={uploadId}", Ct), Ct));
            await http.DeleteAsync($"race/k?uploadId={uploadId}", Ct);

            Assert.Equal(0, server.Store.TotalBytes);
        }
    }

    [Fact]
    public async Task Reset_under_load_does_not_leave_phantom_bytes()
    {
        using var server = new InMemoryBucketServer(o => o.CreateBuckets("busy"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(async () =>
        {
            using var http = server.CreateHttpClient();
            for (var i = 0; !cts.IsCancellationRequested; i++)
            {
                using var put = await http.PutAsync($"busy/{w}-{i % 5}", new ByteArrayContent(new byte[10]), CancellationToken.None);
                using var delete = await http.DeleteAsync($"busy/{w}-{(i + 2) % 5}", CancellationToken.None);
            }
        })).ToArray();

        for (var i = 0; i < 200; i++)
        {
            server.Reset();
            await Task.Yield();
        }

        await cts.CancelAsync();
        await Task.WhenAll(writers);

        var held = server.Store.Buckets.Sum(b => b.Objects.Sum(o => o.Size));
        Assert.Equal(held, server.Store.TotalBytes);
    }

    [Fact]
    public async Task A_duplicate_or_empty_upload_id_fails_loudly()
    {
        using var duplicate = new InMemoryBucketServer(o =>
        {
            o.UploadIdGenerator = () => "same";
            o.CreateBuckets("ids");
        });
        using var http = duplicate.CreateHttpClient();
        using var first = await http.PostAsync("ids/a?uploads", null, Ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => http.PostAsync("ids/b?uploads", null, Ct));
        Assert.Contains("'same'", error.Message);

        using var empty = new InMemoryBucketServer(o =>
        {
            o.UploadIdGenerator = () => "";
            o.CreateBuckets("ids");
        });
        using var emptyHttp = empty.CreateHttpClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() => emptyHttp.PostAsync("ids/a?uploads", null, Ct));
    }

    [Fact]
    public async Task Reset_restarts_request_ids_and_a_zero_limit_refuses_every_write()
    {
        using var server = new InMemoryBucketServer(o =>
        {
            o.MaxTotalBytes = 0;
            o.CreateBuckets("zero");
        });
        using var http = server.CreateHttpClient();

        using var before = await http.GetAsync("zero?list-type=2", Ct);
        server.Reset();
        using var after = await http.GetAsync("zero?list-type=2", Ct);
        Assert.Equal(before.Headers.GetValues("x-amz-request-id"), after.Headers.GetValues("x-amz-request-id"));

        server.Seed("zero", "empty", ""); // zero bytes still fit
        Assert.Throws<InvalidOperationException>(() => server.Seed("zero", "k", "x"));
    }

    [Fact]
    public void Core_part_reupload_racing_abort_releases_each_part_once()
    {
        var store = new ObjectStore();
        store.TryCreateBucket("race", out var bucket);
        for (var i = 0; i < 2_000; i++)
        {
            var upload = bucket.CreateMultipartUpload("k", InMemoryBucket.Core.ObjectAttributes.Default, null, null);
            using var start = new Barrier(3);
            Parallel.Invoke(
                () => { start.SignalAndWait(); bucket.UploadPart(upload, 1, new byte[10], ChecksumSet.Empty); },
                () => { start.SignalAndWait(); bucket.UploadPart(upload, 1, new byte[10], ChecksumSet.Empty); },
                () => { start.SignalAndWait(); bucket.AbortUpload(upload.UploadId); });
            bucket.AbortUpload(upload.UploadId);

            Assert.Equal(0, store.TotalBytes);
        }
    }

    [Fact]
    public void Core_create_bucket_racing_clear_never_binds_a_live_bucket_to_a_discarded_budget()
    {
        var store = new ObjectStore();
        for (var i = 0; i < 2_000; i++)
        {
            using var start = new Barrier(2);
            Parallel.Invoke(
                () => { start.SignalAndWait(); store.TryCreateBucket("fresh", out _); },
                () => { start.SignalAndWait(); store.Clear(); });

            if (store.TryGetBucket("fresh", out var bucket))
            {
                bucket.Put("k", new byte[10], InMemoryBucket.Core.ObjectAttributes.Default, ChecksumSet.Empty);
                Assert.Equal(10, store.TotalBytes);
            }

            store.Clear();
        }
    }

    private static Task<UploadPartResponse> UploadPart(IAmazonS3 s3, string key, string uploadId, int number, int size, string bucket = "limited")
        => s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartNumber = number,
            InputStream = new MemoryStream(new byte[size]),
        }, Ct);
}
