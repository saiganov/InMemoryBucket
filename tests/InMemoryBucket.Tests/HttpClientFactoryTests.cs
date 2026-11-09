using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using InMemoryBucket.AwsSdk;
using InMemoryBucket.Http;
using Microsoft.Extensions.DependencyInjection;

namespace InMemoryBucket.Tests;

/// <summary>
/// <c>AddInMemoryBucketHandler()</c>: IHttpClientFactory clients answered by the DI singleton server.
/// </summary>
public sealed class HttpClientFactoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Named_client_reaches_the_server_and_registers_it_when_missing()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("s3", c => c.BaseAddress = new Uri("https://s3.eu-west-1.amazonaws.com/"))
            .AddInMemoryBucketHandler(o => o.CreateBuckets("files"));

        await using var provider = services.BuildServiceProvider();
        var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient("s3");

        using var put = await http.PutAsync("files/a.txt", new StringContent("hello"), Ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var server = provider.GetRequiredService<InMemoryBucketServer>();
        Assert.Equal("hello", Encoding.UTF8.GetString(server.GetObject("files", "a.txt")!.Data.Span));
        Assert.Equal("hello", await http.GetStringAsync("files/a.txt", Ct));
    }

    [Fact]
    public async Task Handler_and_AddInMemoryBucket_share_one_server_in_either_order()
    {
        var awsFirst = new ServiceCollection();
        awsFirst.AddInMemoryBucket(o => o.CreateBuckets("shared"));
        awsFirst.AddHttpClient("s3").AddInMemoryBucketHandler();

        var handlerFirst = new ServiceCollection();
        handlerFirst.AddHttpClient("s3").AddInMemoryBucketHandler(o => o.CreateBuckets("shared"));
        handlerFirst.AddInMemoryBucket();

        foreach (var services in new[] { awsFirst, handlerFirst })
        {
            await using var provider = services.BuildServiceProvider();
            var s3 = provider.GetRequiredService<IAmazonS3>();
            var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient("s3");

            await s3.PutObjectAsync(new PutObjectRequest { BucketName = "shared", Key = "k", ContentBody = "from-sdk" }, Ct);

            Assert.Equal("from-sdk", await http.GetStringAsync("http://s3.amazonaws.com/shared/k", Ct));
            Assert.Single(services, d => d.ServiceType == typeof(InMemoryBucketServer));
        }
    }

    [Fact]
    public void Options_after_the_server_is_registered_throw_instead_of_being_ignored()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("a").AddInMemoryBucketHandler(o => o.CreateBuckets("a"));

        var handler = Assert.Throws<InvalidOperationException>(() => services.AddHttpClient("b").AddInMemoryBucketHandler(o => o.CreateBuckets("b")));
        Assert.Contains("already registered", handler.Message);
        Assert.Throws<InvalidOperationException>(() => services.AddInMemoryBucket(o => o.CreateBuckets("c")));

        services.AddHttpClient("b").AddInMemoryBucketHandler();   // without options: fine
        services.AddInMemoryBucket();
    }

    [Fact]
    public async Task Delegating_handlers_still_run_in_front_of_the_fake()
    {
        var services = new ServiceCollection();
        var counter = new CountingHandler();
        services.AddHttpClient("s3", c => c.BaseAddress = new Uri("http://s3.amazonaws.com/"))
            .AddHttpMessageHandler(() => counter)
            .AddInMemoryBucketHandler(o => o.CreateBuckets("files"));

        await using var provider = services.BuildServiceProvider();
        var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient("s3");

        using var response = await http.GetAsync("files/missing", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, counter.Calls);
    }

    [Fact]
    public async Task A_keyed_server_does_not_count_as_the_registered_one()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton("other", (_, _) => new InMemoryBucketServer());
        services.AddInMemoryBucket(o => o.CreateBuckets("files"));         // options accepted: no unkeyed server yet
        services.AddHttpClient("s3").AddInMemoryBucketHandler();

        await using var provider = services.BuildServiceProvider();
        var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient("s3");

        using var response = await http.PutAsync("http://s3.amazonaws.com/files/k", new StringContent("v"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(provider.GetRequiredService<InMemoryBucketServer>().GetObject("files", "k"));
        Assert.Empty(provider.GetRequiredKeyedService<InMemoryBucketServer>("other").Store.Buckets);
    }

    [Fact]
    public async Task Typed_client_reaches_the_server()
    {
        var services = new ServiceCollection();
        services.AddHttpClient<FilesClient>(c => c.BaseAddress = new Uri("https://storage.example.com/files/"))
            .AddInMemoryBucketHandler(o => o.CreateBuckets("files"));

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<FilesClient>();

        await client.Http.PutAsync("report.csv", new StringContent("a,b"), Ct);

        Assert.Equal("a,b", Encoding.UTF8.GetString(provider.GetRequiredService<InMemoryBucketServer>().GetObject("files", "report.csv")!.Data.Span));
    }

    private sealed class FilesClient(HttpClient http)
    {
        public HttpClient Http { get; } = http;
    }

    private sealed class CountingHandler : DelegatingHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
