# InMemoryBucket

An in-process S3 emulator for .NET tests. It is an `HttpMessageHandler` that speaks the S3 REST protocol over an in-memory store, with no Docker and no ports. Production code keeps its real S3 client, and the test swaps only the transport.

```csharp
using InMemoryBucket;

using var s3 = new InMemoryBucketServer(o => o.CreateBuckets("uploads"));
HttpClient http = s3.CreateHttpClient();       // or s3.CreateHandler() for any HTTP-based S3 client

s3.Seed("uploads", "config.json", "{}", new() { ContentType = "application/json" });
// ... run the code under test ...
var keys = s3.ListKeys("uploads");
```

It also offers fault injection (`s3.Faults`), a request log (`s3.Requests`), SigV4 validation, presigned-URL expiry, `MaxTotalBytes` and deterministic upload ids.

Related packages:

| Package | Use it for |
|---|---|
| `InMemoryBucket.AwsSdk` | `CreateClient()` and `AddInMemoryBucket()` for AWSSDK.S3 |
| `InMemoryBucket.Http` | `AddInMemoryBucketHandler()` for `IHttpClientFactory` and the MinIO SDK |
| `InMemoryBucket.Hosting` | the same server over Kestrel, for other processes and the AWS CLI |

Documentation, supported operations and samples: [https://github.com/saiganov/InMemoryBucket](https://github.com/saiganov/InMemoryBucket).
