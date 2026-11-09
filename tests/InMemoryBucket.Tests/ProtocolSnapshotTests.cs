using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using InMemoryBucket.Core;
using InMemoryBucket.Protocol;
using Microsoft.Extensions.Time.Testing;

namespace InMemoryBucket.Tests;

/// <summary>
/// Approved snapshots (see <see cref="Snapshot"/>) of the status, headers and body the server answers for every <see cref="S3Operation"/> and
/// every <see cref="S3ErrorCode"/>, so any change to the wire format shows up in review as a diff of a .verified.txt file.
/// Requests go straight to <see cref="InMemoryBucketServer.HandleAsync"/>: no SDK, so the snapshots do not depend on its version.
/// A fake clock and a counter for upload ids make every response byte-identical on any machine.
/// </summary>
public sealed class ProtocolSnapshotTests
{
    private const string Bucket = "snap";

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> Operations => new(Enum.GetNames<S3Operation>());

    public static TheoryData<string> ErrorCodes => new(ErrorCodeFields().Select(f => f.Name));

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Operation_response(string operation)
    {
        var op = Enum.Parse<S3Operation>(operation);
        Assert.True(Scenarios.TryGetValue(op, out var scenario), $"S3Operation.{op} has no snapshot scenario: add one to {nameof(Scenarios)}.");

        using var server = NewServer();
        var response = await scenario(new Client(server));

        Assert.Equal(op, server.Requests.Last!.Operation);
        if (op is S3Operation.Unknown or S3Operation.UploadPartCopy)
        {
            Assert.Equal(501, response.StatusCode); // not implemented by the fake
        }
        else
        {
            Assert.True(response.StatusCode < 300, $"The {op} scenario should snapshot a success response, got {response.StatusCode}.");
        }

        Snapshot.Match($"Operation.{op}", Render(response));
    }

    [Theory]
    [MemberData(nameof(ErrorCodes))]
    public async Task Error_response(string name)
    {
        var code = (S3ErrorCode)ErrorCodeFields().Single(f => f.Name == name).GetValue(null)!;
        using var server = NewServer();
        server.Faults.Always(r => r.Operation == S3Operation.GetObject, code);

        var response = await new Client(server).Send("GET", $"/{Bucket}/key.txt");

        Assert.Equal(code.HttpStatus, response.StatusCode);
        Snapshot.Match($"Error.{name}", Render(response));
    }

    [Fact]
    public async Task Error_response_to_HEAD_has_no_body()
    {
        using var server = NewServer();

        var response = await new Client(server).Send("HEAD", $"/{Bucket}/missing.txt");

        Assert.Equal(404, response.StatusCode);
        Snapshot.Match("Error.HeadObject.NoSuchKey", Render(response));
    }

    [Fact]
    public void Every_approved_snapshot_belongs_to_a_test()
    {
        var expected = Enum.GetNames<S3Operation>().Select(n => $"Operation.{n}")
            .Concat(ErrorCodeFields().Select(f => $"Error.{f.Name}"))
            .Append("Error.HeadObject.NoSuchKey")
            .Select(n => n + ".verified.txt")
            .Order(StringComparer.Ordinal);
        var actual = Directory.GetFiles(Snapshot.Directory(), "*.verified.txt").Select(Path.GetFileName).Order(StringComparer.Ordinal);

        Assert.Equal(expected, actual!);
    }

    private static IEnumerable<FieldInfo> ErrorCodeFields()
        => typeof(S3ErrorCode).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(S3ErrorCode));

    private static InMemoryBucketServer NewServer()
    {
        var next = 0;
        var clock = new FakeTimeProvider(Start);
        var server = new InMemoryBucketServer(o =>
        {
            o.TimeProvider = clock;
            o.UploadIdGenerator = () => $"upload-{++next}";
            o.CreateBuckets(Bucket);
        });
        server.Seed(Bucket, "key.txt", "hello snapshot", new() { ContentType = "text/plain", Metadata = { ["owner"] = "alice" }, Tags = { ["team"] = "storage" } });
        server.Seed(Bucket, "dir/a.json", "{}", new() { ContentType = "application/json" });
        clock.Advance(TimeSpan.FromMinutes(1));
        return server;
    }

    /// <summary>One request per operation; setup requests before the last one are not snapshotted.</summary>
    private static readonly Dictionary<S3Operation, Func<Client, Task<S3Response>>> Scenarios = new()
    {
        [S3Operation.Unknown] = c => c.Send("PUT", $"/{Bucket}", "versioning", body: "<VersioningConfiguration><Status>Enabled</Status></VersioningConfiguration>"),
        [S3Operation.ListBuckets] = c => c.Send("GET", "/"),
        [S3Operation.CreateBucket] = c => c.Send("PUT", "/new-bucket"),
        [S3Operation.HeadBucket] = c => c.Send("HEAD", $"/{Bucket}"),
        [S3Operation.DeleteBucket] = async c =>
        {
            await c.Send("PUT", "/empty-bucket");
            return await c.Send("DELETE", "/empty-bucket");
        },
        [S3Operation.GetBucketLocation] = c => c.Send("GET", $"/{Bucket}", "location"),
        [S3Operation.GetBucketAcl] = c => c.Send("GET", $"/{Bucket}", "acl"),
        [S3Operation.ListObjects] = c => c.Send("GET", $"/{Bucket}", "delimiter=%2F&max-keys=10"),
        [S3Operation.ListObjectsV2] = c => c.Send("GET", $"/{Bucket}", "list-type=2&delimiter=%2F&fetch-owner=true"),
        [S3Operation.ListMultipartUploads] = async c =>
        {
            await c.Send("POST", $"/{Bucket}/big.bin", "uploads");
            return await c.Send("GET", $"/{Bucket}", "uploads");
        },
        [S3Operation.DeleteObjects] = c => c.Send("POST", $"/{Bucket}", "delete", body:
            "<Delete><Object><Key>key.txt</Key></Object><Object><Key>missing.txt</Key></Object></Delete>", contentMd5: true),
        [S3Operation.PutObject] = c => c.Send("PUT", $"/{Bucket}/new.txt", body: "new content",
            headers: new() { ["Content-Type"] = "text/plain", ["x-amz-meta-color"] = "blue", ["x-amz-checksum-crc32"] = Crc32Base64("new content") }),
        [S3Operation.CopyObject] = c => c.Send("PUT", $"/{Bucket}/copy.txt", headers: new() { ["x-amz-copy-source"] = $"/{Bucket}/key.txt" }),
        [S3Operation.GetObject] = c => c.Send("GET", $"/{Bucket}/key.txt"),
        [S3Operation.HeadObject] = c => c.Send("HEAD", $"/{Bucket}/key.txt"),
        [S3Operation.DeleteObject] = c => c.Send("DELETE", $"/{Bucket}/key.txt"),
        [S3Operation.CreateMultipartUpload] = c => c.Send("POST", $"/{Bucket}/big.bin", "uploads", headers: new() { ["Content-Type"] = "application/octet-stream" }),
        [S3Operation.UploadPart] = async c =>
        {
            await c.Send("POST", $"/{Bucket}/big.bin", "uploads");
            return await c.Send("PUT", $"/{Bucket}/big.bin", "partNumber=1&uploadId=upload-1", body: "part one");
        },
        [S3Operation.UploadPartCopy] = async c =>
        {
            await c.Send("POST", $"/{Bucket}/big.bin", "uploads");
            return await c.Send("PUT", $"/{Bucket}/big.bin", "partNumber=1&uploadId=upload-1", headers: new() { ["x-amz-copy-source"] = $"/{Bucket}/key.txt" });
        },
        [S3Operation.CompleteMultipartUpload] = async c =>
        {
            await c.Send("POST", $"/{Bucket}/big.bin", "uploads");
            var part = await c.Send("PUT", $"/{Bucket}/big.bin", "partNumber=1&uploadId=upload-1", body: "only part");
            var etag = part.Headers["ETag"];
            return await c.Send("POST", $"/{Bucket}/big.bin", "uploadId=upload-1",
                body: $"<CompleteMultipartUpload><Part><PartNumber>1</PartNumber><ETag>{etag}</ETag></Part></CompleteMultipartUpload>");
        },
        [S3Operation.AbortMultipartUpload] = async c =>
        {
            await c.Send("POST", $"/{Bucket}/big.bin", "uploads");
            return await c.Send("DELETE", $"/{Bucket}/big.bin", "uploadId=upload-1");
        },
        [S3Operation.ListParts] = async c =>
        {
            await c.Send("POST", $"/{Bucket}/big.bin", "uploads");
            await c.Send("PUT", $"/{Bucket}/big.bin", "partNumber=1&uploadId=upload-1", body: "part one");
            return await c.Send("GET", $"/{Bucket}/big.bin", "uploadId=upload-1");
        },
        [S3Operation.GetObjectTagging] = c => c.Send("GET", $"/{Bucket}/key.txt", "tagging"),
        [S3Operation.PutObjectTagging] = c => c.Send("PUT", $"/{Bucket}/key.txt", "tagging",
            body: "<Tagging><TagSet><Tag><Key>env</Key><Value>test</Value></Tag></TagSet></Tagging>"),
        [S3Operation.DeleteObjectTagging] = c => c.Send("DELETE", $"/{Bucket}/key.txt", "tagging"),
    };

    /// <summary>The base64 big-endian CRC32 S3 expects in x-amz-checksum-crc32.</summary>
    private static string Crc32Base64(string text)
    {
        var crc = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, System.IO.Hashing.Crc32.HashToUInt32(Encoding.UTF8.GetBytes(text)));
        return Convert.ToBase64String(crc);
    }

    /// <summary>Status line, headers in name order, and the body (XML indented) as one stable text.</summary>
    private static string Render(S3Response response)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"HTTP {response.StatusCode}\n");
        foreach (var (name, value) in response.Headers.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
        {
            text.Append(CultureInfo.InvariantCulture, $"{name}: {value}\n");
        }

        if (response.DeclaredContentLength is { } declared)
        {
            text.Append(CultureInfo.InvariantCulture, $"(declared Content-Length: {declared})\n");
        }

        text.Append('\n');
        var body = Encoding.UTF8.GetString(response.Body.Span);
        if (body.StartsWith('<'))
        {
            var xml = XDocument.Parse(body);
            body = (xml.Declaration is null ? "" : xml.Declaration + "\n") + xml.Root;
        }

        text.Append(body);
        return text.ToString().Replace("\r\n", "\n");
    }

    private sealed class Client(InMemoryBucketServer server)
    {
        public async Task<S3Response> Send(string method, string path, string query = "", string? body = null,
            Dictionary<string, string>? headers = null, bool contentMd5 = false)
        {
            var bytes = body is null ? [] : Encoding.UTF8.GetBytes(body);
            var all = new Dictionary<string, string>(headers ?? [], StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = server.Options.Host,
                ["Content-Length"] = bytes.Length.ToString(CultureInfo.InvariantCulture),
            };
            if (contentMd5)
            {
                all["Content-MD5"] = Convert.ToBase64String(MD5.HashData(bytes));
            }

            var request = S3Request.Parse(method, server.Options.Host, path, query, all, bytes, server.Options.Host);
            return await server.HandleAsync(request, TestContext.Current.CancellationToken);
        }
    }
}
