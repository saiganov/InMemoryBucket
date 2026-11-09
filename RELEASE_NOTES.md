#### 0.1.0 (preview) ####

First preview. The public API is tracked in `PublicAPI.*.txt` and checked by package validation; it may still change before 1.0.

- `InMemoryBucket`: an in-process S3 server behind an `HttpMessageHandler`. Buckets, PutObject, GetObject (ranges, conditional headers), HeadObject, DeleteObject, DeleteObjects, CopyObject, ListObjects v1 and v2, multipart uploads, ListMultipartUploads, ListParts, object tags, conditional writes (`If-None-Match: *`, `If-Match`), server-side encryption headers. S3's key, metadata and tag limits apply. Features the fake cannot honour answer 501 instead of being ignored.
- `aws-chunked` uploads with trailing checksums (the AWS SDK for .NET 4.x default), Content-MD5 and CRC32, CRC32C, CRC64NVME, SHA-1 and SHA-256 checksums, presigned URLs with expiry, and an optional strict mode that verifies SigV4 signatures.
- Test hooks: `Seed()` with `SeedOptions`, `GetObject()`/`ListKeys()`, fault injection (`Faults`), a request log (`Requests`), `ILogger` tracing, `Reset()`, `MaxTotalBytes`, a `TimeProvider` and deterministic upload ids for repeatable responses.
- `InMemoryBucket.AwsSdk`: `AddInMemoryBucket()` replaces `IAmazonS3` in DI; works with AWSSDK.S3 4.0.13.1 or later 4.x, including `TransferUtility`.
- `InMemoryBucket.Http`: `AddInMemoryBucketHandler()` makes the fake the primary handler of an `IHttpClientFactory` client, for the MinIO SDK or a hand-written S3 client.
- `InMemoryBucket.Hosting`: serves the fake over Kestrel on a loopback port, or mounts it on an ASP.NET Core pipeline, for the AWS CLI and other processes.
- Libraries target net8.0 and depend on the lowest versions they work with (`Microsoft.Extensions.*` 8.0.0).
- The same contract test suite runs against the fake (in-process and over TCP) and a real MinIO; approved snapshots guard the wire format of every operation and error code. Differences from S3 are listed in KNOWN_DIFFERENCES.md.
