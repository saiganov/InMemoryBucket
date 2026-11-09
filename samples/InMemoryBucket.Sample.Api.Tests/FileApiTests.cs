using System.Net;
using System.Net.Http.Json;
using System.Text;
using InMemoryBucket.AwsSdk;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace InMemoryBucket.Sample.Api.Tests;

/// <summary>
/// The pattern InMemoryBucket exists for: the real app, the real AmazonS3Client, and only the transport swapped.
/// </summary>
public sealed class FileApiTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _api;
    private readonly InMemoryBucketServer _s3;

    public FileApiTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.ConfigureTestServices(services =>
            {
                // Replaces the IAmazonS3 that AddAWSService<IAmazonS3>() registered in Program.cs.
                services.AddInMemoryBucket(o => o.CreateBuckets("sample-files"));
            });
        });
        _api = _factory.CreateClient();
        _s3 = _factory.Services.GetRequiredService<InMemoryBucketServer>();
    }

    public void Dispose()
    {
        _api.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Upload_download_list_and_delete_go_through_the_fake()
    {
        var ct = TestContext.Current.CancellationToken;

        using var upload = new StringContent("hello from the api", Encoding.UTF8, "text/plain");
        var put = await _api.PutAsync("/files/greeting.txt", upload, ct);
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);

        var get = await _api.GetAsync("/files/greeting.txt", ct);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("text/plain", get.Content.Headers.ContentType?.MediaType);
        Assert.Equal("hello from the api", await get.Content.ReadAsStringAsync(ct));

        var names = await _api.GetFromJsonAsync<string[]>("/files", ct);
        Assert.Equal(["greeting.txt"], names ?? []);

        var delete = await _api.DeleteAsync("/files/greeting.txt", ct);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var gone = await _api.GetAsync("/files/greeting.txt", ct);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Test_can_seed_and_inspect_the_store_directly()
    {
        var ct = TestContext.Current.CancellationToken;
        _s3.Seed("sample-files", "seeded.json", "{\"ok\":true}", new() { ContentType = "application/json" });

        var get = await _api.GetAsync("/files/seeded.json", ct);
        Assert.Equal("application/json", get.Content.Headers.ContentType?.MediaType);
        Assert.Equal("{\"ok\":true}", await get.Content.ReadAsStringAsync(ct));

        using var upload = new ByteArrayContent([1, 2, 3]);
        upload.Headers.ContentType = new("application/octet-stream");
        await _api.PutAsync("/files/raw.bin", upload, ct);

        var stored = _s3.GetObject("sample-files", "raw.bin");
        Assert.NotNull(stored);
        Assert.Equal([1, 2, 3], stored.Data.ToArray());
        Assert.Equal("sample-api", stored.Attributes.UserMetadata["uploaded-by"]);
    }
}
