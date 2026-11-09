using System.Net;
using System.Text;
using InMemoryBucket.AwsSdk;
using InMemoryBucket.Core;
using InMemoryBucket.Protocol;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace InMemoryBucket.Sample.Tests;

/// <summary>
/// The same API, with the test hooks: S3 failures injected on demand, the log of what the app sent, a fake clock,
/// and <see cref="InMemoryBucketServer.Reset"/> between scenarios.
/// </summary>
public sealed class TestHooksTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Now);
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _api;
    private readonly InMemoryBucketServer _s3;

    public TestHooksTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            host.ConfigureTestServices(services =>
                services.AddInMemoryBucket(o =>
                {
                    o.CreateBuckets("sample-files");
                    o.TimeProvider = _clock;   // LastModified, Date headers and presigned expiry follow the fake clock
                })));
        _api = _factory.CreateClient();
        _s3 = _factory.Services.GetRequiredService<InMemoryBucketServer>();
    }

    public void Dispose()
    {
        _api.Dispose();
        _factory.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_injected_S3_error_shows_how_the_app_handles_it()
    {
        _s3.Seed("sample-files", "report.txt", "q1");
        _s3.Faults.Once(r => r.Operation == S3Operation.GetObject && r.Key == "report.txt", S3ErrorCode.SlowDown);

        // The sample API does not handle S3 errors, so throttling becomes a 500 for its caller.
        var failed = await _api.GetAsync("/files/report.txt", Ct);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.True(_s3.Requests.Last!.Faulted);
        Assert.Equal("SlowDown", _s3.Requests.Last.ErrorCode);

        // Once means once: the next request goes through.
        Assert.Equal("q1", await _api.GetStringAsync("/files/report.txt", Ct));
    }

    [Fact]
    public async Task The_request_log_shows_what_the_app_sent_to_S3()
    {
        using var upload = new StringContent("hello", Encoding.UTF8, "text/plain");
        await _api.PutAsync("/files/greeting.txt", upload, Ct);
        await _api.GetAsync("/files/missing.txt", Ct);

        Assert.Collection(
            _s3.Requests,
            put =>
            {
                Assert.Equal(S3Operation.PutObject, put.Operation);
                Assert.Equal("greeting.txt", put.Key);
                Assert.Equal("sample-api", put.Header("x-amz-meta-uploaded-by"));
                Assert.Equal(200, put.StatusCode);
            },
            get =>
            {
                Assert.Equal(S3Operation.GetObject, get.Operation);
                Assert.Equal("NoSuchKey", get.ErrorCode);
            });
    }

    [Fact]
    public async Task Stored_objects_carry_the_fake_clock_time()
    {
        _clock.Advance(TimeSpan.FromHours(1));
        using var upload = new StringContent("{}", Encoding.UTF8, "application/json");
        await _api.PutAsync("/files/settings.json", upload, Ct);

        Assert.Equal(Now.AddHours(1), _s3.GetObject("sample-files", "settings.json")!.LastModified);
    }

    [Fact]
    public async Task Reset_gives_the_next_scenario_an_empty_store_with_the_configured_buckets()
    {
        _s3.Seed("sample-files", "old.txt", "x");
        _s3.Faults.Always(r => r.Operation == S3Operation.ListObjectsV2, S3ErrorCode.InternalError);

        _s3.Reset();   // objects, fault rules and the request log are gone; "sample-files" exists again

        var list = await _api.GetAsync("/files", Ct);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal("[]", await list.Content.ReadAsStringAsync(Ct));
        Assert.Single(_s3.Requests);
    }
}
