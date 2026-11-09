using System.Text;
using InMemoryBucket.Http;
using InMemoryBucket.Sample.Minio;
using Microsoft.Extensions.DependencyInjection;
using Minio;

namespace InMemoryBucket.Sample.Minio.Tests;

/// <summary>
/// The application registers its MinIO client in DI with a named HttpClient; the test swaps only that client's transport.
/// </summary>
public sealed class DependencyInjectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>What the application's Program.cs would register: a real endpoint, real credentials, a named HttpClient.</summary>
    private static ServiceCollection ProductionServices()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("minio");
        services.AddSingleton<IMinioClient>(sp => new MinioClient()
            .WithEndpoint("minio.internal:9000")
            .WithCredentials("app-access-key", "app-secret-key")
            .WithHttpClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("minio"))
            .Build());
        services.AddSingleton(sp => new MediaLibrary(sp.GetRequiredService<IMinioClient>(), "media"));
        return services;
    }

    [Fact]
    public async Task One_line_routes_the_applications_minio_client_to_the_fake()
    {
        var services = ProductionServices();
        services.AddHttpClient("minio").AddInMemoryBucketHandler(o => o.CreateBuckets("media"));

        await using var provider = services.BuildServiceProvider();
        var library = provider.GetRequiredService<MediaLibrary>();
        var s3 = provider.GetRequiredService<InMemoryBucketServer>();

        await library.EnsureBucketAsync(Ct);
        await library.AddAsync("holiday", "beach.jpg", new MemoryStream("sand"u8.ToArray()), 4, "image/jpeg", Ct);

        Assert.Equal("sand", Encoding.UTF8.GetString((await library.ReadAsync("holiday", "beach.jpg", Ct))!));
        Assert.Equal("image/jpeg", s3.GetObject("media", "holiday/beach.jpg")!.Attributes.ContentType);
        Assert.Contains(s3.Requests, r => r.Key == "holiday/beach.jpg" && r.AccessKey == "app-access-key");
    }
}
