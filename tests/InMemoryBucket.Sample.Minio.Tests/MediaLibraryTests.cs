using System.Net;
using System.Text;
using InMemoryBucket.Sample.Minio;
using Minio;

namespace InMemoryBucket.Sample.Minio.Tests;

/// <summary>
/// The MinIO SDK needs no adapter: give it an HttpClient bound to the fake. No AWS SDK anywhere in this test project.
/// </summary>
public sealed class MediaLibraryTests : IDisposable
{
    private readonly InMemoryBucketServer _s3 = new();   // no bucket configured: the library creates its own
    private readonly HttpClient _http;
    private readonly IMinioClient _minio;
    private readonly MediaLibrary _library;

    public MediaLibraryTests()
    {
        _http = _s3.CreateHttpClient();
        _minio = new MinioClient()
            .WithEndpoint(_s3.Options.Host)
            .WithCredentials("inmemorybucket", "inmemorybucket")
            .WithHttpClient(_http)
            .Build();
        _library = new MediaLibrary(_minio, "media");
    }

    public void Dispose()
    {
        _minio.Dispose();
        _http.Dispose();
        _s3.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Albums_round_trip_through_the_minio_sdk()
    {
        await _library.EnsureBucketAsync(Ct);
        await _library.EnsureBucketAsync(Ct);                                  // idempotent
        Assert.True(_s3.Store.TryGetBucket("media", out _));

        await AddAsync("holiday", "beach.jpg", "sand");
        await AddAsync("holiday", "sunset.jpg", "orange");
        await AddAsync("work", "badge.png", "lanyard");

        var holiday = await _library.ListAlbumAsync("holiday", Ct);
        Assert.Equal(["holiday/beach.jpg", "holiday/sunset.jpg"], holiday.Select(i => i.Path));
        Assert.All(holiday, i => Assert.NotNull(i.LastModified));

        Assert.Equal("sand", Encoding.UTF8.GetString((await _library.ReadAsync("holiday", "beach.jpg", Ct))!));
        Assert.Null(await _library.ReadAsync("holiday", "missing.jpg", Ct));

        var stored = _s3.GetObject("media", "work/badge.png")!;
        Assert.Equal("image/png", stored.Attributes.ContentType);
        Assert.Equal("work", stored.Attributes.UserMetadata["album"]);
    }

    [Fact]
    public async Task Share_links_work_without_credentials()
    {
        await _library.EnsureBucketAsync(Ct);
        await AddAsync("holiday", "beach.jpg", "sand");

        var link = await _library.ShareAsync("holiday", "beach.jpg", TimeSpan.FromMinutes(10));
        using var anonymous = _s3.CreateHttpClient();
        var response = await anonymous.GetAsync(link, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("sand", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("inmemorybucket", _s3.Requests.Last!.AccessKey);
    }

    [Fact]
    public async Task Deleting_an_album_removes_only_its_files()
    {
        await _library.EnsureBucketAsync(Ct);
        await AddAsync("holiday", "beach.jpg", "sand");
        await AddAsync("holiday", "sunset.jpg", "orange");
        await AddAsync("work", "badge.png", "lanyard");

        Assert.Equal(2, await _library.DeleteAlbumAsync("holiday", Ct));
        Assert.Equal(0, await _library.DeleteAlbumAsync("holiday", Ct));
        Assert.Equal(["work/badge.png"], _s3.ListKeys("media"));
    }

    private async Task AddAsync(string album, string fileName, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var stream = new MemoryStream(bytes);
        var contentType = fileName.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "image/jpeg";
        await _library.AddAsync(album, fileName, stream, bytes.Length, contentType, Ct);
    }
}
