using Amazon.S3;
using Amazon.S3.Model;
using InMemoryBucket.Sample.Api;

var builder = WebApplication.CreateBuilder(args);

// Production wiring: region and credentials come from configuration / the environment.
builder.Services.AddDefaultAWSOptions(builder.Configuration.GetAWSOptions());
builder.Services.AddAWSService<IAmazonS3>();
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.AddSingleton<FileStore>();

var app = builder.Build();

app.MapPut("/files/{name}", async (string name, HttpRequest request, FileStore files, CancellationToken ct) =>
{
    await files.SaveAsync(name, request.Body, request.ContentType ?? "application/octet-stream", ct);
    return Results.Created($"/files/{name}", null);
});

app.MapGet("/files/{name}", async (string name, FileStore files, CancellationToken ct) =>
{
    var file = await files.OpenAsync(name, ct);
    return file is null ? Results.NotFound() : Results.Stream(file.Content, file.ContentType);
});

app.MapGet("/files", (FileStore files, CancellationToken ct) => files.ListAsync(ct));

app.MapDelete("/files/{name}", async (string name, FileStore files, CancellationToken ct) =>
{
    await files.DeleteAsync(name, ct);
    return Results.NoContent();
});

app.Run();

namespace InMemoryBucket.Sample.Api
{
    public sealed class StorageOptions
    {
        public string Bucket { get; set; } = "sample-files";
    }

    public sealed record StoredFile(Stream Content, string ContentType);

    /// <summary>The application's own S3 code. Nothing here knows about InMemoryBucket.</summary>
    public sealed class FileStore(IAmazonS3 s3, Microsoft.Extensions.Options.IOptions<StorageOptions> options)
    {
        private string Bucket => options.Value.Bucket;

        public async Task SaveAsync(string name, Stream content, string contentType, CancellationToken ct)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            buffer.Position = 0;

            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = Bucket,
                Key = name,
                InputStream = buffer,
                ContentType = contentType,
                Metadata = { ["uploaded-by"] = "sample-api" },
            }, ct);
        }

        public async Task<StoredFile?> OpenAsync(string name, CancellationToken ct)
        {
            try
            {
                var response = await s3.GetObjectAsync(Bucket, name, ct);
                return new StoredFile(response.ResponseStream, response.Headers.ContentType);
            }
            catch (AmazonS3Exception e) when (e.ErrorCode == "NoSuchKey")
            {
                return null;
            }
        }

        public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct)
        {
            var names = new List<string>();
            var request = new ListObjectsV2Request { BucketName = Bucket };
            ListObjectsV2Response page;
            do
            {
                page = await s3.ListObjectsV2Async(request, ct);
                names.AddRange((page.S3Objects ?? []).Select(o => o.Key));
                request.ContinuationToken = page.NextContinuationToken;
            }
            while (page.IsTruncated == true);

            return names;
        }

        public Task DeleteAsync(string name, CancellationToken ct) => s3.DeleteObjectAsync(Bucket, name, ct);
    }
}

// Lets WebApplicationFactory<Program> find the entry point.
public partial class Program;
