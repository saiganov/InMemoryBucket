using Amazon.S3;
using Amazon.S3.Model;
using InMemoryBucket.Sample.Presigned;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDefaultAWSOptions(builder.Configuration.GetAWSOptions());
builder.Services.AddAWSService<IAmazonS3>();
builder.Services.Configure<UploadOptions>(builder.Configuration.GetSection("Uploads"));
builder.Services.AddSingleton<UploadTickets>();

var app = builder.Build();

// 1. The client asks for a ticket: where to PUT the file and until when.
app.MapPost("/uploads", async (NewUpload body, UploadTickets tickets, CancellationToken ct) =>
    Results.Ok(await tickets.IssueUploadAsync(body.FileName, body.ContentType, ct)));

// 2. After uploading straight to S3, the client confirms; the API only checks the object exists and records its size.
app.MapPost("/uploads/{id}/complete", async (string id, UploadTickets tickets, CancellationToken ct) =>
{
    var completed = await tickets.CompleteAsync(id, ct);
    return completed is null ? Results.NotFound(new { error = "Nothing was uploaded for this ticket." }) : Results.Ok(completed);
});

// 3. Anyone with the id gets a short-lived download link.
app.MapGet("/downloads/{id}", async (string id, UploadTickets tickets, CancellationToken ct) =>
{
    var link = await tickets.IssueDownloadAsync(id, ct);
    return link is null ? Results.NotFound() : Results.Ok(link);
});

app.Run();

namespace InMemoryBucket.Sample.Presigned
{
    public sealed class UploadOptions
    {
        public string Bucket { get; set; } = "user-uploads";

        public TimeSpan UploadWindow { get; set; } = TimeSpan.FromMinutes(15);

        public TimeSpan DownloadWindow { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>HTTPS in production; HTTP for local endpoints such as MinIO or an InMemoryBucket host.</summary>
        public Protocol Protocol { get; set; } = Protocol.HTTPS;
    }

    public sealed record NewUpload(string FileName, string ContentType);

    public sealed record UploadTicket(string Id, string Url, string Method, string ContentType, DateTimeOffset ExpiresAt);

    public sealed record CompletedUpload(string Id, string FileName, long Size, string ContentType);

    public sealed record DownloadLink(string Url, DateTimeOffset ExpiresAt);

    /// <summary>The application's S3 code: presigned PUT and GET, HeadObject to confirm. Nothing here knows about InMemoryBucket.</summary>
    public sealed class UploadTickets(IAmazonS3 s3, IOptions<UploadOptions> options, TimeProvider clock)
    {
        private readonly UploadOptions _options = options.Value;

        public async Task<UploadTicket> IssueUploadAsync(string fileName, string contentType, CancellationToken ct)
        {
            var id = Guid.NewGuid().ToString("N");
            var expires = clock.GetUtcNow().Add(_options.UploadWindow);
            var url = await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
            {
                BucketName = _options.Bucket,
                Key = Key(id),
                Verb = HttpVerb.PUT,
                ContentType = contentType,
                Expires = expires.UtcDateTime,
                Protocol = _options.Protocol,
                Metadata = { ["file-name"] = fileName },
            });
            return new UploadTicket(id, url, "PUT", contentType, expires);
        }

        public async Task<CompletedUpload?> CompleteAsync(string id, CancellationToken ct)
        {
            try
            {
                var head = await s3.GetObjectMetadataAsync(_options.Bucket, Key(id), ct);
                return new CompletedUpload(id, head.Metadata["x-amz-meta-file-name"] ?? id, head.ContentLength, head.Headers.ContentType);
            }
            catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        public async Task<DownloadLink?> IssueDownloadAsync(string id, CancellationToken ct)
        {
            if (await CompleteAsync(id, ct) is not { } file)
            {
                return null;
            }

            var expires = clock.GetUtcNow().Add(_options.DownloadWindow);
            var url = await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
            {
                BucketName = _options.Bucket,
                Key = Key(id),
                Verb = HttpVerb.GET,
                Expires = expires.UtcDateTime,
                Protocol = _options.Protocol,
                ResponseHeaderOverrides = { ContentDisposition = $"attachment; filename=\"{file.FileName}\"" },
            });
            return new DownloadLink(url, expires);
        }

        private static string Key(string id) => $"incoming/{id}";
    }
}

public partial class Program;
