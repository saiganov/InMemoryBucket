using System.Net;
using System.Net.Http.Json;
using System.Text;
using InMemoryBucket.AwsSdk;
using InMemoryBucket.Sample.Presigned;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace InMemoryBucket.Sample.Presigned.Tests;

/// <summary>
/// Two clients in one test: the API (via WebApplicationFactory) and "the browser" (a plain HttpClient bound to the fake),
/// which uploads to the presigned URL the API handed out. The fake checks the URL's expiry on its own clock.
/// </summary>
public sealed class PresignedUploadTests : IDisposable
{
    // Starts at the real time: the SDK signs with the wall clock, and strict mode rejects signatures more than 15 minutes off.
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _api;
    private readonly HttpClient _browser;
    private readonly InMemoryBucketServer _s3;

    public PresignedUploadTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(_clock);
            services.AddInMemoryBucket(o =>
            {
                o.CreateBuckets("user-uploads");
                o.TimeProvider = _clock;
                o.ValidateSignatures = true;   // a tampered or hand-built URL must fail here as it would on S3
            });
        }));
        _api = _factory.CreateClient();
        _s3 = _factory.Services.GetRequiredService<InMemoryBucketServer>();
        _browser = _s3.CreateHttpClient();
    }

    public void Dispose()
    {
        _browser.Dispose();
        _api.Dispose();
        _factory.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Browser_uploads_straight_to_s3_then_the_api_confirms_and_links()
    {
        var ticket = await IssueAsync("photo.jpg", "image/jpeg");
        Assert.Equal("PUT", ticket.Method);
        Assert.Equal(_clock.GetUtcNow().AddMinutes(15), ticket.ExpiresAt);

        var bytes = Encoding.UTF8.GetBytes("not really a jpeg");
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new("image/jpeg");
        using var request = new HttpRequestMessage(HttpMethod.Put, ticket.Url) { Content = content };
        request.Headers.Add("x-amz-meta-file-name", "photo.jpg");          // signed into the URL, so the browser must send it
        var upload = await _browser.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        var complete = await _api.PostAsync($"/uploads/{ticket.Id}/complete", null, Ct);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var completed = (await complete.Content.ReadFromJsonAsync<CompletedUpload>(Ct))!;
        Assert.Equal(("photo.jpg", bytes.LongLength, "image/jpeg"), (completed.FileName, completed.Size, completed.ContentType));

        var link = (await _api.GetFromJsonAsync<DownloadLink>($"/downloads/{ticket.Id}", Ct))!;
        var download = await _browser.GetAsync(link.Url, Ct);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("not really a jpeg", await download.Content.ReadAsStringAsync(Ct));
        Assert.Equal("attachment; filename=\"photo.jpg\"", download.Content.Headers.ContentDisposition?.ToString());

        var stored = _s3.GetObject("user-uploads", $"incoming/{ticket.Id}")!;
        Assert.Equal("photo.jpg", stored.Attributes.UserMetadata["file-name"]);
    }

    [Fact]
    public async Task Completing_before_uploading_is_404()
    {
        var ticket = await IssueAsync("empty.txt", "text/plain");
        var complete = await _api.PostAsync($"/uploads/{ticket.Id}/complete", null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, complete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _api.GetAsync($"/downloads/{ticket.Id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Upload_url_stops_working_after_the_window()
    {
        var ticket = await IssueAsync("late.txt", "text/plain");
        _clock.Advance(TimeSpan.FromMinutes(16));

        var response = await _browser.PutAsync(ticket.Url, new StringContent("too late", Encoding.UTF8, "text/plain"), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("<Code>AccessDenied</Code>", body);
        Assert.Contains("Request has expired", body);
        Assert.Null(_s3.GetObject("user-uploads", $"incoming/{ticket.Id}"));
    }

    [Fact]
    public async Task Changing_the_key_or_content_type_breaks_the_signature()
    {
        var ticket = await IssueAsync("mine.txt", "text/plain");

        var otherKey = await _browser.PutAsync(ticket.Url.Replace(ticket.Id, "someone-elses-id"), new StringContent("x", Encoding.UTF8, "text/plain"), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, otherKey.StatusCode);
        Assert.Contains("<Code>SignatureDoesNotMatch</Code>", await otherKey.Content.ReadAsStringAsync(Ct));

        var wrongType = await _browser.PutAsync(ticket.Url, new StringContent("<html>", Encoding.UTF8, "text/html"), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, wrongType.StatusCode);

        Assert.Empty(_s3.ListKeys("user-uploads"));
        Assert.Equal(2, _s3.Requests.Count(r => r.ErrorCode == "SignatureDoesNotMatch"));
    }

    private async Task<UploadTicket> IssueAsync(string fileName, string contentType)
    {
        var response = await _api.PostAsJsonAsync("/uploads", new NewUpload(fileName, contentType), Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UploadTicket>(Ct))!;
    }
}
