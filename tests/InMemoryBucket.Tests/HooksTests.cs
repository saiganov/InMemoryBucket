using System.Diagnostics;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using InMemoryBucket.AwsSdk;
using InMemoryBucket.Core;
using InMemoryBucket.Hooks;
using InMemoryBucket.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace InMemoryBucket.Tests;

/// <summary>Faults, the request log and tracing: what a real server cannot give a test.</summary>
public sealed class HooksTests : IDisposable
{
    private readonly InMemoryBucketServer _server = new(o => o.CreateBuckets("uploads"));
    private readonly AmazonS3Client _s3;

    public HooksTests()
    {
        _s3 = _server.CreateClient();
    }

    public void Dispose()
    {
        _s3.Dispose();
        _server.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task PutAsync(string key, string body = "x")
        => _s3.PutObjectAsync(new PutObjectRequest { BucketName = "uploads", Key = key, ContentBody = body }, Ct);

    [Fact]
    public async Task Once_fails_the_first_matching_request_then_steps_aside()
    {
        _server.Faults.Once(r => r.Operation == S3Operation.PutObject, S3ErrorCode.SlowDown);

        var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => PutAsync("a"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal("SlowDown", error.ErrorCode);

        await PutAsync("a");
        Assert.NotNull(_server.GetObject("uploads", "a"));
        Assert.Equal(0, _server.Faults.Rules[0].Remaining);
    }

    [Fact]
    public async Task Always_applies_until_cleared_and_other_requests_pass()
    {
        _server.Faults.Always(r => r.Key?.StartsWith("locked/", StringComparison.Ordinal) == true, S3ErrorCode.AccessDenied);

        for (var i = 0; i < 3; i++)
        {
            var error = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => PutAsync("locked/" + i));
            Assert.Equal("AccessDenied", error.ErrorCode);
        }

        await PutAsync("open/1");

        _server.Faults.Clear();
        await PutAsync("locked/now-fine");
        Assert.Equal(["locked/now-fine", "open/1"], _server.ListKeys("uploads"));
    }

    [Fact]
    public async Task Times_counts_down_across_matching_requests()
    {
        _server.Faults.Times(2, r => r.Operation == S3Operation.GetObject, S3ErrorCode.InternalError, "flaky backend");
        await PutAsync("k");

        var first = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => _s3.GetObjectAsync("uploads", "k", Ct));
        Assert.Equal("InternalError", first.ErrorCode);
        Assert.Equal("flaky backend", first.Message);
        await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => _s3.GetObjectAsync("uploads", "k", Ct));

        using var ok = await _s3.GetObjectAsync("uploads", "k", Ct);
        Assert.Equal(HttpStatusCode.OK, ok.HttpStatusCode);
    }

    [Fact]
    public async Task Fault_errors_are_real_S3_errors_the_SDK_retries_when_allowed()
    {
        using var retrying = _server.CreateClient(c => c.MaxErrorRetry = 2);
        _server.Faults.Once(r => r.Operation == S3Operation.PutObject, S3ErrorCode.SlowDown);

        await retrying.PutObjectAsync(new PutObjectRequest { BucketName = "uploads", Key = "retried", ContentBody = "x" }, Ct);

        var puts = _server.Requests.Where(r => r.Operation == S3Operation.PutObject).ToList();
        Assert.Equal(2, puts.Count);
        Assert.True(puts[0].Faulted);
        Assert.Equal(503, puts[0].StatusCode);
        Assert.Equal(200, puts[1].StatusCode);
    }

    [Fact]
    public async Task Delay_holds_the_request_and_then_serves_it()
    {
        _server.Faults.Delay(r => r.Operation == S3Operation.GetObject, TimeSpan.FromMilliseconds(300));
        await PutAsync("slow");

        var watch = Stopwatch.StartNew();
        using var response = await _s3.GetObjectAsync("uploads", "slow", Ct);
        watch.Stop();

        Assert.Equal(HttpStatusCode.OK, response.HttpStatusCode);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(250), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Delay_honours_the_client_timeout()
    {
        _server.Faults.Delay(r => true, TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _s3.GetObjectAsync("uploads", "never", cts.Token));
    }

    [Fact]
    public async Task Throw_surfaces_as_a_transport_failure_not_an_S3_error()
    {
        _server.Faults.Throw(r => r.Bucket == "uploads", new HttpRequestException("connection reset"));

        var error = await Assert.ThrowsAnyAsync<Exception>(() => PutAsync("k"));

        Assert.IsNotType<AmazonS3Exception>(error);
        var inner = error;
        while (inner is not HttpRequestException && inner.InnerException is not null)
        {
            inner = inner.InnerException;
        }

        Assert.IsType<HttpRequestException>(inner);
        Assert.Equal("connection reset", inner.Message);

        var record = Assert.Single(_server.Requests);
        Assert.True(record.Faulted);
        Assert.Equal(0, record.StatusCode);
    }

    [Fact]
    public async Task Requests_records_operation_bucket_key_headers_status_and_access_key()
    {
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = "uploads",
            Key = "docs/a.txt",
            ContentBody = "hello",
            ContentType = "text/plain",
            Metadata = { ["owner"] = "test" },
        }, Ct);
        await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => _s3.GetObjectAsync("uploads", "missing", Ct));

        Assert.Equal(2, _server.Requests.Count);
        var put = _server.Requests.First();
        Assert.Equal(S3Operation.PutObject, put.Operation);
        Assert.Equal("PUT", put.Method);
        Assert.Equal("uploads", put.Bucket);
        Assert.Equal("docs/a.txt", put.Key);
        Assert.Equal("text/plain", put.Header("Content-Type"));
        Assert.Equal("test", put.Header("x-amz-meta-owner"));
        Assert.Equal(5, put.ContentLength);
        Assert.Null(put.Body);
        Assert.Equal(200, put.StatusCode);
        Assert.Null(put.ErrorCode);
        Assert.Equal("inmemorybucket", put.AccessKey);
        Assert.True(put.Duration > TimeSpan.Zero);

        var get = _server.Requests.Last!;
        Assert.Equal(S3Operation.GetObject, get.Operation);
        Assert.Equal(404, get.StatusCode);
        Assert.Equal("NoSuchKey", get.ErrorCode);
        Assert.False(get.Faulted);
    }

    [Fact]
    public async Task Requests_can_capture_bodies_and_is_bounded()
    {
        using var server = new InMemoryBucketServer(o =>
        {
            o.CreateBuckets("hooks-b");
            o.CaptureBodies = true;
            o.RequestLogCapacity = 3;
        });
        using var s3 = server.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = "hooks-b", Key = "k" + i, ContentBody = "body" + i }, Ct);
        }

        Assert.Equal(3, server.Requests.Count);
        Assert.Equal(["k2", "k3", "k4"], server.Requests.Select(r => r.Key));
        Assert.Equal("body4", System.Text.Encoding.UTF8.GetString(server.Requests.Last!.Body!.Value.Span));
    }

    [Fact]
    public async Task Reset_clears_store_rules_and_log()
    {
        _server.Faults.Always(r => true, S3ErrorCode.AccessDenied);
        await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => PutAsync("k"));

        _server.Reset();

        Assert.Empty(_server.Faults.Rules);
        Assert.Empty(_server.Requests);
        await PutAsync("k");
        Assert.Equal(["k"], _server.ListKeys("uploads"));
    }

    [Fact]
    public async Task Logger_receives_one_debug_line_per_request_and_traces_on_trace_level()
    {
        var logger = new CapturingLogger(LogLevel.Trace);
        using var server = new InMemoryBucketServer(o =>
        {
            o.CreateBuckets("hooks-b");
            o.Logger = logger;
        });
        using var s3 = server.CreateClient();

        await s3.PutObjectAsync(new PutObjectRequest { BucketName = "hooks-b", Key = "k", ContentBody = "x" }, Ct);
        await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => s3.GetObjectAsync("hooks-b", "nope", Ct));

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Trace && e.Message.Contains("--> PutObject") && e.Message.Contains("Authorization: <redacted>"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Trace && e.Message.Contains("<-- 404") && e.Message.Contains("<Code>NoSuchKey</Code>"));

        var debugOnly = new CapturingLogger(LogLevel.Debug);
        server.Options.Logger = debugOnly;
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = "hooks-b", Key = "k2", ContentBody = "x" }, Ct);
        var line = Assert.Single(debugOnly.Entries);
        Assert.Equal(LogLevel.Debug, line.Level);
        Assert.Contains("PutObject PUT /hooks-b/k2 -> 200", line.Message);
    }

    [Fact]
    public async Task Presigned_url_works_until_it_expires()
    {
        await PutAsync("signed", "secret");
        using var http = _server.CreateHttpClient();

        var valid = await _s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest { BucketName = "uploads", Key = "signed", Expires = DateTime.UtcNow.AddMinutes(5) });
        var ok = await http.GetAsync(valid, Ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("secret", await ok.Content.ReadAsStringAsync(Ct));

        var expired = await _s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest { BucketName = "uploads", Key = "signed", Expires = DateTime.UtcNow.AddSeconds(-30) });
        var denied = await http.GetAsync(expired, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var body = await denied.Content.ReadAsStringAsync(Ct);
        Assert.Contains("<Code>AccessDenied</Code>", body);

        Assert.Equal("inmemorybucket", _server.Requests.Last!.AccessKey);
        Assert.Equal(403, _server.Requests.Last!.StatusCode);
    }

    [Fact]
    public async Task Presigned_expiry_follows_the_servers_clock()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var server = new InMemoryBucketServer(o =>
        {
            o.CreateBuckets("hooks-b");
            o.TimeProvider = clock;
        });
        using var s3 = server.CreateClient();
        using var http = server.CreateHttpClient();
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = "hooks-b", Key = "k", ContentBody = "x" }, Ct);

        var url = await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest { BucketName = "hooks-b", Key = "k", Expires = DateTime.UtcNow.AddMinutes(10) });
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(url, Ct)).StatusCode);

        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(url, Ct)).StatusCode);
    }

    [Fact]
    public async Task Unknown_amz_headers_are_reported_once_as_a_warning()
    {
        var logger = new CapturingLogger(LogLevel.Warning);
        using var server = new InMemoryBucketServer(o =>
        {
            o.CreateBuckets("hooks-w");
            o.Logger = logger;
        });
        using var http = server.CreateHttpClient();

        for (var i = 0; i < 2; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, "hooks-w/k") { Content = new StringContent("x") };
            request.Headers.Add("x-amz-grant-read", "uri=http://acs.amazonaws.com/groups/global/AllUsers");
            request.Headers.Add("x-amz-meta-known", "metadata is fine");
            Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(request, Ct)).StatusCode);
        }

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("x-amz-grant-read", warning.Message);
    }

    [Fact]
    public async Task Aws_sdk_uploads_with_signed_trailers_raise_no_unknown_header_warning()
    {
        var logger = new CapturingLogger(LogLevel.Warning);
        using var server = new InMemoryBucketServer(o =>
        {
            o.CreateBuckets("hooks-t");
            o.Logger = logger;
        });
        using var s3 = server.CreateClient();

        // The SDK sends a flexible checksum as an aws-chunked trailer, signed with x-amz-trailer-signature.
        await s3.PutObjectAsync(new Amazon.S3.Model.PutObjectRequest
        {
            BucketName = "hooks-t",
            Key = "k",
            ContentBody = "payload",
            ChecksumAlgorithm = Amazon.S3.ChecksumAlgorithm.CRC32,
        }, Ct);

        Assert.Contains(server.Requests, r => r.Operation == S3Operation.PutObject && r.Headers.ContainsKey("x-amz-trailer-signature"));
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    private sealed class CapturingLogger(LogLevel minimum) : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
