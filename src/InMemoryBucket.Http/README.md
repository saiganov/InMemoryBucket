# InMemoryBucket.Http

Connects `IHttpClientFactory` clients to [InMemoryBucket](https://github.com/saiganov/InMemoryBucket), an in-process S3 fake for tests. It works for the MinIO .NET SDK and for hand-written S3 clients, and does not depend on the AWS SDK.

```csharp
using InMemoryBucket.Http;

// After the application's own registrations, for example in ConfigureTestServices:
services.AddHttpClient("minio").AddInMemoryBucketHandler(o => o.CreateBuckets("media"));
```

The fake becomes the client's primary handler, and delegating handlers still run. The server is the `InMemoryBucketServer` singleton in the container, shared with `AddInMemoryBucket()`, so you can resolve it to seed and inspect the store. Path-style requests work with any host. For virtual-hosted requests, set `o.Host` to the production S3 host.

Documentation and samples: [https://github.com/saiganov/InMemoryBucket](https://github.com/saiganov/InMemoryBucket).
