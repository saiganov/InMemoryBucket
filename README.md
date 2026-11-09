# InMemoryBucket

In-process S3 for .NET tests. A real `AmazonS3Client` talks to an in-memory store through an `HttpMessageHandler`: no Docker, no ports, no hand-written `Mock<IAmazonS3>`.

> Status: 0.1 preview. The S3 operations below pass the same contract test suite against InMemoryBucket (in-process and over Kestrel) and against a real MinIO; both the AWS SDK for .NET and the MinIO .NET SDK are covered. The public API may still change before 1.0.

## Why

Testing code that uses S3 today means either Testcontainers with MinIO or LocalStack (needs Docker, seconds to start, often unavailable on CI) or mocking `IAmazonS3` by hand (hundreds of methods, no real behaviour). InMemoryBucket emulates the S3 REST protocol itself, so the production code keeps its real `IAmazonS3` and only the transport is swapped.

## Usage

```csharp
using InMemoryBucket;
using InMemoryBucket.AwsSdk;

// Without DI
using var s3 = new InMemoryBucketServer(o => o.CreateBuckets("uploads"));
IAmazonS3 client = s3.CreateClient();           // a real AmazonS3Client
var transfer = new TransferUtility(client);     // multipart works too

// ASP.NET Core and WebApplicationFactory
builder.ConfigureTestServices(services =>
{
    services.AddInMemoryBucket(o => o.CreateBuckets("uploads", "avatars"));
});

// Seed and inspect without going through HTTP. Seed takes bytes (byte[], "..."u8, ReadOnlySpan<byte>), a string (UTF-8) or a Stream.
s3.Seed("uploads", "a/b.png", bytes, new() { ContentType = "image/png" });
s3.Seed("uploads", "old.json", "{}", new()
{
    ContentType = "application/json",
    Metadata = { ["owner"] = "alice" },
    Tags = { ["team"] = "billing" },
    LastModified = DateTimeOffset.UtcNow.AddDays(-30),
});
var stored = s3.GetObject("uploads", "a/b.png");
var keys = s3.ListKeys("uploads", prefix: "a/");
var pending = s3.Store.GetBucket("uploads").Uploads;   // read-only view: buckets, objects, in-progress multipart uploads
```

`Seed` writes only into a bucket that exists (`CreateBuckets(...)` or CreateBucket through a client) and throws `InvalidOperationException` otherwise, so a typo in a bucket name fails the test instead of creating a second bucket. `GetObject` and `ListKeys` throw the same way for an unknown bucket and return null or an empty list only for a missing key. S3's key, metadata and tag limits apply.

Any HTTP-based S3 client works: `s3.CreateHttpClient()` returns an `HttpClient` bound to the fake, and `s3.CreateHandler()` a raw `HttpMessageHandler`. The MinIO .NET SDK, for example:

```csharp
var minio = new MinioClient()
    .WithEndpoint(s3.Options.Host)
    .WithCredentials("inmemorybucket", "inmemorybucket")
    .WithHttpClient(s3.CreateHttpClient())
    .Build();
```

When the application takes its `HttpClient` from `IHttpClientFactory`, one line in the test swaps that client's transport. It comes from the `InMemoryBucket.Http` package, which does not depend on the AWS SDK:

```csharp
using InMemoryBucket.Http;

// Program.cs registers services.AddHttpClient("minio") and builds its MinioClient with that client.
builder.ConfigureTestServices(services =>
{
    services.AddHttpClient("minio").AddInMemoryBucketHandler(o => o.CreateBuckets("media"));
});
```

Call it after the production registrations, as here. The fake becomes the client's primary handler, and delegating handlers (retries, logging) still run in front of it. `BaseAddress` and the endpoint stay as they are: path-style requests (`https://any-host/bucket/key`, what the MinIO SDK sends) work with any host. Virtual-hosted requests (`https://bucket.host/key`) are recognised only under `Options.Host`, so for such a client set `o.Host` to the production S3 host. The server is the `InMemoryBucketServer` singleton in the container, shared with `AddInMemoryBucket()` and other clients, so resolve it to seed and inspect. Options go to whichever call registers the server first; passing options to a later `AddInMemoryBucket()` or `AddInMemoryBucketHandler()` throws instead of ignoring them.

### Faults, request log, tracing

What a real server cannot give a test: break things on purpose and see what the code under test sent.

```csharp
// Fail the next PutObject with 503 SlowDown, then behave.
s3.Faults.Once(r => r.Operation == S3Operation.PutObject, S3ErrorCode.SlowDown);
// Every GET under "locked/" is 403 until the rule is removed.
s3.Faults.Always(r => r.Key?.StartsWith("locked/") == true, S3ErrorCode.AccessDenied);
// Slow down or break the transport itself.
s3.Faults.Delay(r => r.Operation == S3Operation.GetObject, TimeSpan.FromSeconds(2));
s3.Faults.Throw(r => r.Bucket == "flaky", new IOException("connection reset"));

// Every request, oldest first: operation, method, bucket, key, status, error code, duration, access key.
Assert.Contains(s3.Requests, r => r.Operation == S3Operation.CompleteMultipartUpload && r.StatusCode == 200);
s3.Options.CaptureBodies = true;    // keep bodies too (off by default)
s3.Reset();                         // drop everything: store, rules, log; recreate the configured buckets
```

### Memory limit and repeatable responses

```csharp
var counter = 0;
using var s3 = new InMemoryBucketServer(o =>
{
    o.MaxTotalBytes = 256 * 1024 * 1024;                                     // a write past it throws InvalidOperationException
    o.TimeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)); // fixed Date and LastModified
    o.UploadIdGenerator = () => $"upload-{Interlocked.Increment(ref counter)}"; // fixed upload ids; thread-safe for parallel uploads
});
long held = s3.Store.TotalBytes;                              // objects plus in-progress multipart parts
```

`MaxTotalBytes` counts object data and the parts of in-progress multipart uploads; a completed or aborted upload gives its parts back, and a deleted bucket drops its uploads. Completing an upload needs room for its parts and the assembled object at the same time, as memory does, so leave about twice the largest multipart object as headroom. Going over the limit is a bug in the test, not an S3 error: in process the client call throws `InvalidOperationException` with the current size and the limit; over `InMemoryBucket.Hosting` the client gets a 500. Both options are read when the server is constructed. With a fake clock and a deterministic `UploadIdGenerator`, two runs of the same test get byte-identical responses: `x-amz-request-id` is a per-server counter that `Reset()` restarts.

`Options.Logger` takes any `ILogger`: Debug gives one line per request, Trace adds headers (Authorization redacted) and XML bodies. `AddInMemoryBucket()` wires the host's logging automatically. `Options.TimeProvider` drives `LastModified`, `Date` and presigned-URL expiry, so a `FakeTimeProvider` makes time-based tests deterministic.

### Presigned URLs and signatures

Presigned URLs from either SDK work against `s3.CreateHttpClient()` and expire when S3 says they do (`EnforcePresignedExpiry`, on by default, uses `Options.TimeProvider`).

By default any credentials are accepted, which is what most tests want. `ValidateSignatures = true` recomputes every AWS Signature Version 4 (Authorization header and presigned query) against `Options.Credentials` and answers `SignatureDoesNotMatch`, `InvalidAccessKeyId`, `AuthorizationHeaderMalformed` (wrong region), `RequestTimeTooSkewed` (clock more than 15 minutes off), `XAmzContentSHA256Mismatch` or `AccessDenied` (anonymous) exactly as S3 does. The default key pair is `inmemorybucket` / `inmemorybucket`; `o.UseCredentials("AKIA...", "secret")` replaces it and `CreateClient()` follows. Chunk signatures inside `aws-chunked` bodies are not verified. The skew check compares the request's `x-amz-date` with `Options.TimeProvider`, so a `FakeTimeProvider` used together with strict mode should start near the real time (SDKs sign with the wall clock) or `ClockSkewTolerance` should be raised.

### Over the network: `InMemoryBucket.Hosting`

When the client cannot take an `HttpMessageHandler` (another process, a container, the AWS CLI), serve the same server on a loopback port:

```csharp
await using var host = await InMemoryBucketHost.StartAsync(o => o.CreateBuckets("uploads"));
var client = new AmazonS3Client(new BasicAWSCredentials("inmemorybucket", "inmemorybucket"),
    new AmazonS3Config { ServiceURL = host.BaseAddress.ToString(), ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
host.Server.Faults...; host.Server.Requests...;   // same server, same hooks
```

`app.UseInMemoryBucket(server)` mounts the terminal middleware on an existing ASP.NET Core pipeline instead. Path-style addressing is the safe choice; `bucket.localhost:port` virtual-hosted requests are understood too. Note that the AWS SDK presigns with `https` unless `GetPreSignedUrlRequest.Protocol = Protocol.HTTP`.

## What works

- Buckets: Create, Head, Delete, List, GetBucketLocation, GetBucketAcl (what `AmazonS3Util.DoesS3BucketExistV2Async` probes).
- Objects: Put (conditional writes with `If-None-Match: *` and `If-Match`), Get (with `Range` and conditional headers), Head, Delete, DeleteObjects, CopyObject (COPY and REPLACE metadata directives, conditional copy headers), ListObjectsV2 with prefix, delimiter, paging, `start-after` and `encoding-type=url`, ListObjects v1 with markers.
- Server-side encryption headers: objects report SSE-S3 (`AES256`) by default; `aws:kms` with a key id and bucket key is stored and echoed. Nothing is actually encrypted.
- Object tags: `x-amz-tagging` on Put, Copy (`x-amz-tagging-directive`) and multipart Initiate, Get/Put/DeleteObjectTagging, `x-amz-tagging-count` on Get/Head, S3's limits answered with `InvalidTag`.
- Multipart: Initiate, UploadPart, Complete, Abort, ListParts, ListMultipartUploads (prefix, delimiter, markers); ETags computed as S3 does (`md5-of-md5s-N`).
- `aws-chunked` request bodies with trailing checksums, the default upload mode of AWSSDK.S3 4.x; CRC32, CRC32C, SHA1, SHA256 and Content-MD5 are verified and answered with `BadDigest` on mismatch.
- Errors in the S3 XML format with the same codes and HTTP statuses, so `AmazonS3Exception.ErrorCode` matches production.
- Presigned URLs (SigV4 and SigV2 expiry), optional strict SigV4 verification, fault injection, request log, `ILogger` tracing, `TimeProvider`.
- Hosting on Kestrel for clients outside the process.
- Anything not implemented answers `501 NotImplemented` naming the operation, never a silent success.

Not yet: UploadPartCopy, versioning, bucket tagging, object lock, bucket policies, SSE-C, event notifications. See the architecture plan for the roadmap and [KNOWN_DIFFERENCES.md](KNOWN_DIFFERENCES.md) for where S3, MinIO and the fake disagree.

## Samples

Each sample is a small application written the production way (no InMemoryBucket reference) with a test project that swaps in the fake. Together they cover the scenarios the library is for:

| Sample | Scenario | What the tests show |
|---|---|---|
| [`Sample.Api`](samples/InMemoryBucket.Sample.Api) | Minimal API storing files in S3 via `AddAWSService<IAmazonS3>()` | `WebApplicationFactory` + `AddInMemoryBucket()`: only the transport changes; seed and inspect the store directly. The test hooks on the same API: an injected S3 error, the request log, a `FakeTimeProvider`, `Reset()` between scenarios. |
| [`Sample.Presigned`](samples/InMemoryBucket.Sample.Presigned) | API hands out presigned PUT/GET URLs; the browser talks to S3 directly | Two clients in one test (API and "browser"), uploads through the presigned URL, expiry on the fake clock, tampered URLs rejected with `ValidateSignatures`. |
| [`Sample.Minio`](samples/InMemoryBucket.Sample.Minio) | Media library on the MinIO .NET SDK | No AWS SDK at all: `MinioClient` with an `HttpClient` bound to the fake, streaming uploads, listings, share links, bulk delete; and the same client from DI through `AddInMemoryBucketHandler()`. |
| [`Sample.Standalone`](samples/InMemoryBucket.Sample.Standalone) | Console host for clients outside the process (AWS CLI, boto3, containers) | `InMemoryBucket.Hosting` on a real port with custom credentials and strict signatures; an external `AmazonS3Client` and a raw HTTP client use it. `dotnet run -- --port 9000 --bucket uploads --strict` prints the AWS CLI commands. |

The shortest version, from `Sample.Api`:

```csharp
var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
    host.ConfigureTestServices(services =>
        services.AddInMemoryBucket(o => o.CreateBuckets("sample-files"))));

var server = factory.Services.GetRequiredService<InMemoryBucketServer>();
server.Seed("sample-files", "seeded.json", bytes, new() { ContentType = "application/json" });
```

## How the library is tested

One contract suite, `tests/InMemoryBucket.ContractTests`, is written against `IAmazonS3` only and compiled into two test projects:

- `InMemoryBucket.Tests` runs it against the fake in-process on every push and pull request, next to the hook, signature and MinIO SDK tests.
- `InMemoryBucket.Hosting.Tests` runs it again over TCP through Kestrel with strict signatures on.
- `InMemoryBucket.Tests.MinIO` runs it against MinIO nightly ([workflow](.github/workflows/nightly.yml)). It starts MinIO through Testcontainers, or uses a running server when `INMEMORYBUCKET_MINIO_ENDPOINT` (plus `INMEMORYBUCKET_MINIO_ACCESS_KEY` / `INMEMORYBUCKET_MINIO_SECRET_KEY`) is set. `INMEMORYBUCKET_MINIO_IMAGE` overrides the image (default `cgr.dev/chainguard/minio:latest`, since MinIO left Docker Hub and quay.io no longer allows anonymous pulls) and `INMEMORYBUCKET_MINIO_COMMAND` the container command.

Where MinIO and AWS legitimately differ the test branches on a flag of `S3Backend`; nothing is skipped. The differences are listed in [KNOWN_DIFFERENCES.md](KNOWN_DIFFERENCES.md).

The wire format is pinned by approved snapshots in `tests/InMemoryBucket.Tests/Snapshots`: status, headers and body for every `S3Operation` and every `S3ErrorCode`, produced through `HandleAsync()` without an SDK, with a fake clock and deterministic upload ids. Any change to an XML element or a header shows up in review as a diff of a `.verified.txt` file. A new operation without a scenario, or a new error code without a snapshot, fails the test. When a run fails it writes `.received.txt` next to the verified file; copy it over if the change is intended, or run the tests once with `INMEMORYBUCKET_ACCEPT_SNAPSHOTS=1` to rewrite them all (ignored on CI).

## Performance

Time to the first green test: from the moment the OS starts a fresh process to a PutObject and GetObject that succeeded (median of 5 runs, images already pulled; the test host's own start-up is not counted on either side):

| | Time |
|---|---:|
| InMemoryBucket | 0.43 s |
| MinIO started by Testcontainers | 3.7 s |

Most of the 0.43 s is runtime, JIT and AWS SDK start-up, which a MinIO test pays too. About 1.2 s of the MinIO time is Testcontainers starting its Ryuk cleanup container before MinIO itself. After that, a test that builds its own server costs well under a millisecond:

| Benchmark (through a real `AmazonS3Client`) | Mean | Allocated |
|---|---:|---:|
| New server, client, PutObject and GetObject, dispose | 0.39 ms | 326 KB |
| PutObject, 1 KB | 0.20 ms | 250 KB |
| GetObject, 1 KB | 0.12 ms | 69 KB |
| PutObject, 1 MB | 7.5 ms | 4.4 MB |
| GetObject, 1 MB | 5.8 ms | 1.5 MB |
| ListObjectsV2 through all 10,000 keys (10 pages) | 69 ms | 41 MB |

Measured on 4 cores of an Intel Xeon at 2.1 GHz, 16 GB, Ubuntu 24.04, .NET 10.0.14, AWSSDK.S3 4.0.13.1, Docker 29, `bitnamilegacy/minio`. The benchmarks are in `benchmarks/InMemoryBucket.Benchmarks` and run by hand, not in CI:

```bash
dotnet run -c Release --project benchmarks/InMemoryBucket.Benchmarks -- --filter '*'   # BenchmarkDotNet
INMEMORYBUCKET_MINIO_IMAGE=bitnamilegacy/minio:latest INMEMORYBUCKET_MINIO_COMMAND=/opt/bitnami/scripts/minio/run.sh \
  dotnet run -c Release --project benchmarks/InMemoryBucket.Benchmarks -- startup       # first green test vs MinIO (needs Docker)
```

Without the two variables `startup` uses the contract tests' default image, `cgr.dev/chainguard/minio`. Pull the MinIO and `testcontainers/ryuk` images before measuring, or the first run includes the download.

## Packages

| Package | Contents |
|---|---|
| `InMemoryBucket.Core` | The store: buckets, objects, multipart uploads, ETags, checksums. No SDK dependency. |
| `InMemoryBucket` | `InMemoryBucketServer`, the `HttpMessageHandler`, S3 REST routing, `aws-chunked` decoding, XML responses. |
| `InMemoryBucket.AwsSdk` | `CreateClient()` and `AddInMemoryBucket()` for AWSSDK.S3 4.0.13.1 or later 4.x. |
| `InMemoryBucket.Http` | `AddInMemoryBucketHandler()` for `IHttpClientFactory`: named and typed `HttpClient`s, the MinIO SDK. No AWS SDK dependency. |
| `InMemoryBucket.Hosting` | `InMemoryBucketHost` and `UseInMemoryBucket()`: the same server over Kestrel. |

### Public API

Only what tests use is public; routing, parsing, ETag and checksum helpers, listing queries and the store's write methods are internal and may change in any release. Every public type is here for a reason:

| Type | Package | Why it is public |
|---|---|---|
| `InMemoryBucketServer`, `InMemoryBucketOptions` | `InMemoryBucket` | The entry point: create a server, configure it, `CreateHandler()`, `CreateHttpClient()`, `Seed()`, `Reset()`. |
| `SeedOptions` | `InMemoryBucket` | Content-Type, metadata, tags and LastModified for `Seed()`. |
| `FaultRules`, `FaultRule`, `FaultAction` (`Error`, `Delay`, `Throw`) | `InMemoryBucket` | Fault injection from tests. |
| `RequestLog`, `RecordedRequest`, `S3Operation` | `InMemoryBucket` | Asserting on what the code under test sent. |
| `S3Request`, `S3Response` (+ `S3Request.Parse`) | `InMemoryBucket` | `HandleAsync()`: putting the fake behind another transport. |
| `S3ErrorCode`, `S3Exception` | `InMemoryBucket.Core` | Error codes for fault rules; the exception the store's read methods throw. |
| `S3Object`, `ObjectAttributes`, `ChecksumSet`, `ChecksumAlgorithm`, `ServerSideEncryption` | `InMemoryBucket.Core` | Immutable snapshots returned by `GetObject()` and the store, for assertions. |
| `ObjectStore`, `Bucket`, `MultipartUpload` | `InMemoryBucket.Core` | Read-only access to the store through `server.Store` (buckets, objects, in-progress uploads); writes go through S3 or `Seed()`. |
| `InMemoryBucketServerExtensions`, `ServiceCollectionExtensions` | `InMemoryBucket.AwsSdk` | `CreateClient()` and `AddInMemoryBucket()`. |
| `HttpClientBuilderExtensions` | `InMemoryBucket.Http` | `AddInMemoryBucketHandler()`. |
| `InMemoryBucketHost`, `InMemoryBucketApplicationBuilderExtensions` | `InMemoryBucket.Hosting` | The server over Kestrel and `UseInMemoryBucket()`. |

## Installing

Packages are on nuget.org. Until 1.0 they are pre-releases, so pass `--prerelease`:

```bash
dotnet add package InMemoryBucket.AwsSdk --prerelease
```

Packages are published only from `v*` tags (for example `v0.1.0-preview.1`), with symbols; pushes to `main` build and test without publishing.

## Building

```bash
dotnet build
dotnet test --project tests/InMemoryBucket.Tests
dotnet test --project tests/InMemoryBucket.Hosting.Tests
dotnet test --project tests/InMemoryBucket.Sample.Tests   # and the other tests/InMemoryBucket.Sample.*.Tests projects
dotnet test --project tests/InMemoryBucket.Tests.MinIO    # needs Docker or INMEMORYBUCKET_MINIO_ENDPOINT
```

Requires the .NET 10 SDK (see `global.json`); libraries target `net8.0`.

`InMemoryBucket.AwsSdk` is built against the lowest supported AWSSDK.S3, 4.0.13.1 (older 4.0.x versions bring an AWSSDK.Core with a known vulnerability, and 4.0.0 fails the presigned-URL tests). CI runs every test project except the MinIO contract run twice, against that version and against the newest 4.x, on every push and pull request and once a day. A failure on the newest 4.x shows as a red check but does not block publishing, since the packages are built from the minimum leg. To do the same locally:

```bash
dotnet build -c Release -p:AwsSdkS3Version='4.*'    # or an exact version
dotnet test --project tests/InMemoryBucket.Tests --no-build -c Release
```

Dependabot opens weekly grouped PRs for NuGet packages and GitHub Actions. It leaves the dependencies the packages declare (AWSSDK.S3, `Microsoft.Extensions.Http`, the `Microsoft.Extensions` abstractions, `System.IO.Hashing`) alone, since those are floors on purpose.

### Changing the public API

Each package under `src/` has `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`, checked by `Microsoft.CodeAnalysis.PublicApiAnalyzers`. Adding, removing or changing a public member fails the build (RS0016/RS0017) until the line is added to or removed from `PublicAPI.Unshipped.txt`; the IDE code fix, or `dotnet format analyzers <project> --diagnostics RS0016 --severity warn`, writes the line for you. When a version is released, move every line from `Unshipped` to the end of `Shipped` (keeping the `#nullable enable` header in both files) in the release commit, so `Unshipped` lists only what changed since the last release. Removed members are marked with `*REMOVED*` in `Unshipped` and the line is deleted from `Shipped` at release. `pack` also runs package validation; a baseline version for it is set after 1.0.0.

## License

MIT.
