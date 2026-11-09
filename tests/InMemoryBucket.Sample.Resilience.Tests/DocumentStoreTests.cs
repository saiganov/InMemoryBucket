using Amazon.S3;
using Amazon.S3.Model;
using InMemoryBucket.AwsSdk;
using InMemoryBucket.Core;
using InMemoryBucket.Protocol;
using InMemoryBucket.Sample.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace InMemoryBucket.Sample.Resilience.Tests;

/// <summary>
/// Fault injection: the store's retry, timeout and fallback paths, which no happy-path test and no mock of IAmazonS3 exercises
/// realistically. The backoff waits on a FakeTimeProvider, so a 3-attempt scenario takes milliseconds.
/// </summary>
public sealed class DocumentStoreTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new();
    private readonly InMemoryBucketServer _s3;
    private readonly AmazonS3Client _client;
    private readonly DocumentStore _store;

    public DocumentStoreTests()
    {
        _s3 = new InMemoryBucketServer(o =>
        {
            o.CreateBuckets("documents");
            o.TimeProvider = _clock;    // injected delays wait on the same fake clock as the store's timeouts
        });
        _client = _s3.CreateClient();   // MaxErrorRetry = 0: the SDK does not retry, the store does
        _store = new DocumentStore(_client, new DocumentStoreOptions { AttemptTimeout = TimeSpan.FromSeconds(2) }, _clock, NullLogger<DocumentStore>.Instance);
    }

    public void Dispose()
    {
        _client.Dispose();
        _s3.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Throttling_is_retried_with_backoff_until_it_succeeds()
    {
        _s3.Faults.Times(2, r => r.Operation == S3Operation.PutObject, S3ErrorCode.SlowDown);

        var save = _store.SaveAsync("a.txt", "first", Ct);
        await AdvanceUntilAsync(save, TimeSpan.FromMilliseconds(100));   // 100 ms, then 200 ms of backoff on the fake clock

        Assert.Equal(3, _store.Attempts);
        Assert.Equal([503, 503, 200], _s3.Requests.Where(r => r.Operation == S3Operation.PutObject).Select(r => r.StatusCode));
        Assert.Equal("first", System.Text.Encoding.UTF8.GetString(_s3.GetObject("documents", "a.txt")!.Data.Span));
    }

    [Fact]
    public async Task Persistent_server_errors_give_up_after_max_attempts()
    {
        _s3.Faults.Always(r => r.Operation == S3Operation.PutObject, S3ErrorCode.InternalError);

        var save = _store.SaveAsync("b.txt", "never", Ct);
        var error = await Assert.ThrowsAsync<DocumentStoreException>(() => AdvanceUntilAsync(save, TimeSpan.FromMilliseconds(100)));

        Assert.Contains("after 3 attempts", error.Message);
        Assert.Equal("InternalError", Assert.IsType<AmazonS3Exception>(error.InnerException).ErrorCode);
        Assert.Equal(3, _s3.Requests.Count(r => r.Operation == S3Operation.PutObject));
    }

    [Fact]
    public async Task Client_errors_are_not_retried()
    {
        _s3.Faults.Always(r => r.Operation == S3Operation.PutObject, S3ErrorCode.AccessDenied);

        var error = await Assert.ThrowsAsync<DocumentStoreException>(() => _store.SaveAsync("c.txt", "forbidden", Ct));

        Assert.Contains("AccessDenied", error.Message);
        Assert.Equal(1, _store.Attempts);
    }

    [Fact]
    public async Task A_slow_response_hits_the_attempt_timeout_and_is_retried()
    {
        _s3.Seed("documents", "slow.txt", "eventually");
        var delayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _s3.Faults.Delay(
            r =>
            {
                if (r.Operation != S3Operation.GetObject)
                {
                    return false;
                }

                delayed.TrySetResult();
                return true;
            },
            TimeSpan.FromSeconds(10),
            times: 1);

        var load = _store.LoadAsync("slow.txt", Ct);

        // The clock moves only at points the test controls. Advancing it on a timer instead races with real work: the attempt
        // timeout could fire before the first GET reaches the fault (so the fault hits the second attempt), or again while the
        // second attempt is still in flight. Either way the store would need three attempts.
        await delayed.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);   // the first GET is held by the 10 s delay
        _clock.Advance(TimeSpan.FromSeconds(2));                       // its 2 s attempt timeout fires

        // Step through the 100 ms backoff until the second attempt starts, then leave the clock alone: that attempt is answered
        // at once and needs no fake time.
        for (var i = 0; i < 200 && _store.Attempts < 2; i++)
        {
            await Task.Delay(5, Ct);
            _clock.Advance(TimeSpan.FromMilliseconds(50));
        }

        var text = await load.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal("eventually", text);
        Assert.Equal(2, _store.Attempts);
    }

    [Fact]
    public async Task Transport_failures_count_as_transient()
    {
        _s3.Faults.Throw(r => r.Operation == S3Operation.PutObject, new IOException("connection reset by peer"), times: 1);

        var save = _store.SaveAsync("d.txt", "made it", Ct);
        await AdvanceUntilAsync(save, TimeSpan.FromMilliseconds(100));

        Assert.Equal(2, _store.Attempts);
        Assert.True(_s3.Requests.First(r => r.Operation == S3Operation.PutObject).Faulted);
        Assert.NotNull(_s3.GetObject("documents", "d.txt"));
    }

    [Fact]
    public async Task Reads_fall_back_to_the_last_good_copy_when_s3_is_down()
    {
        await _store.SaveAsync("e.txt", "cached copy", Ct);
        _s3.Faults.Always(r => r.Operation == S3Operation.GetObject && r.Key == "e.txt", S3ErrorCode.ServiceUnavailable);

        var load = _store.LoadAsync("e.txt", Ct);
        var text = await AdvanceUntilAsync(load, TimeSpan.FromMilliseconds(100));

        Assert.Equal("cached copy", text);
        Assert.Equal(3, _s3.Requests.Count(r => r.Operation == S3Operation.GetObject && r.StatusCode == 503));

        // A missing document is not an outage: no fallback, no retries.
        var missing = await Assert.ThrowsAsync<DocumentStoreException>(() => _store.LoadAsync("nope.txt", Ct));
        Assert.Contains("NoSuchKey", missing.Message);
    }

    [Fact]
    public async Task The_sdks_own_retries_also_survive_an_injected_fault()
    {
        // The alternative policy: let the SDK retry. One SlowDown, then success, in a single call.
        using var retrying = _s3.CreateClient(c => c.MaxErrorRetry = 2);
        _s3.Faults.Once(r => r.Operation == S3Operation.PutObject, S3ErrorCode.SlowDown);

        await retrying.PutObjectAsync(new PutObjectRequest { BucketName = "documents", Key = "sdk.txt", ContentBody = "retried by the sdk" }, Ct);

        Assert.Equal([503, 200], _s3.Requests.Select(r => r.StatusCode));
        Assert.Equal(["attempt=1; max=3", "attempt=2; max=3"], _s3.Requests.Select(r => r.Header("amz-sdk-request")));
    }

    /// <summary>Lets the store's backoff and timeouts, which wait on the fake clock, run to completion.</summary>
    private async Task<T> AdvanceUntilAsync<T>(Task<T> task, TimeSpan step)
    {
        for (var i = 0; i < 100 && !task.IsCompleted; i++)
        {
            await Task.Delay(5, Ct);    // let the awaiting continuation register before the clock moves
            _clock.Advance(step);
        }

        return await task;
    }

    private Task AdvanceUntilAsync(Task task, TimeSpan step)
        => AdvanceUntilAsync(task.ContinueWith(t => { t.GetAwaiter().GetResult(); return 0; }, TaskContinuationOptions.ExecuteSynchronously), step);
}
