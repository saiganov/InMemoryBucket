using System.Text;
using Amazon.S3;
using InMemoryBucket.AwsSdk;
using InMemoryBucket.Core;
using InMemoryBucket.Protocol;
using InMemoryBucket.Sample.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace InMemoryBucket.Sample.Worker.Tests;

/// <summary>
/// A hosted service against the fake: seed the inbox, drive the clock, assert on the store. No sleeps, no Docker.
/// </summary>
public sealed class InboxProcessorTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
    private IHost _host = null!;
    private InMemoryBucketServer _s3 = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Worker:Bucket"] = "documents",
            ["Worker:PollInterval"] = "00:00:10",
        });
        builder.Services.AddAWSService<IAmazonS3>();                    // what Program.cs registers
        builder.Services.AddInboxProcessing(builder.Configuration);     // the worker's own wiring
        builder.Services.AddSingleton<TimeProvider>(_clock);            // the test owns time
        builder.Services.AddInMemoryBucket(o =>
        {
            o.CreateBuckets("documents");
            o.TimeProvider = _clock;                                    // LastModified follows the same clock
        });

        _host = builder.Build();
        _s3 = _host.Services.GetRequiredService<InMemoryBucketServer>();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(Ct);
        _host.Dispose();
    }

    [Fact]
    public async Task One_pass_moves_inbox_files_to_processed_with_a_timestamp()
    {
        _s3.Seed("documents", "inbox/report.txt", "quarterly numbers", new() { ContentType = "text/plain" });
        _s3.Seed("documents", "inbox/notes/todo.txt", "buy milk", new() { ContentType = "text/plain" });
        _s3.Seed("documents", "archive/old.txt", "untouched", new() { ContentType = "text/plain" });

        var processor = _host.Services.GetServices<IHostedService>().OfType<InboxProcessor>().Single();
        var count = await processor.ProcessInboxAsync(Ct);

        Assert.Equal(2, count);
        Assert.Equal(["archive/old.txt", "processed/notes/todo.txt", "processed/report.txt"], _s3.ListKeys("documents"));

        var report = _s3.GetObject("documents", "processed/report.txt")!;
        Assert.Equal("QUARTERLY NUMBERS", Encoding.UTF8.GetString(report.Data.Span));
        Assert.Equal("2026-11-02T12:00:00.0000000+00:00", report.Attributes.UserMetadata["processed-at"]);
        Assert.Equal("inbox/report.txt", report.Attributes.UserMetadata["source-key"]);
        Assert.Equal(_clock.GetUtcNow(), report.LastModified);
    }

    [Fact]
    public async Task The_hosted_loop_wakes_up_on_the_fake_clock()
    {
        await _host.StartAsync(Ct);                                     // first pass runs on start: inbox is empty
        _s3.Seed("documents", "inbox/late.txt", "arrived later", new() { ContentType = "text/plain" });

        // The worker finishes a file with the delete, so wait for that, not just for the processed copy to appear.
        await EventuallyAsync(() => _s3.GetObject("documents", "inbox/late.txt") is null, advance: TimeSpan.FromSeconds(10));

        Assert.NotNull(_s3.GetObject("documents", "processed/late.txt"));
        Assert.Contains(_s3.Requests, r => r.Operation == S3Operation.ListObjectsV2 && r.Query["prefix"] == "inbox/");
    }

    [Fact]
    public async Task A_failed_upload_leaves_the_original_for_the_next_pass()
    {
        _s3.Seed("documents", "inbox/flaky.txt", "try again", new() { ContentType = "text/plain" });
        _s3.Faults.Once(r => r.Operation == S3Operation.PutObject && r.Key == "processed/flaky.txt", S3ErrorCode.InternalError);
        var processor = _host.Services.GetServices<IHostedService>().OfType<InboxProcessor>().Single();

        Assert.Equal(0, await processor.ProcessInboxAsync(Ct));
        Assert.NotNull(_s3.GetObject("documents", "inbox/flaky.txt"));
        Assert.Null(_s3.GetObject("documents", "processed/flaky.txt"));

        Assert.Equal(1, await processor.ProcessInboxAsync(Ct));
        Assert.Equal(["processed/flaky.txt"], _s3.ListKeys("documents"));
        Assert.Equal(2, _s3.Requests.Count(r => r.Operation == S3Operation.PutObject));
    }

    [Fact]
    public async Task Reset_gives_the_next_scenario_a_clean_bucket()
    {
        _s3.Seed("documents", "inbox/a.txt", "a", new() { ContentType = "text/plain" });
        var processor = _host.Services.GetServices<IHostedService>().OfType<InboxProcessor>().Single();
        await processor.ProcessInboxAsync(Ct);
        Assert.NotEmpty(_s3.Requests);

        _s3.Reset();

        Assert.Empty(_s3.ListKeys("documents"));                        // configured bucket is back, empty
        Assert.Empty(_s3.Requests);
        Assert.Equal(0, await processor.ProcessInboxAsync(Ct));
    }

    /// <summary>Advances the fake clock in steps until the condition holds; the worker reacts on its own thread.</summary>
    private async Task EventuallyAsync(Func<bool> condition, TimeSpan advance)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            _clock.Advance(advance);
            await Task.Delay(10, Ct);
        }

        Assert.True(condition(), "Condition not met after advancing the clock 200 times.");
    }
}
