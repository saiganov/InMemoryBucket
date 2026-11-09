# InMemoryBucket.Core

The store behind [InMemoryBucket](https://github.com/saiganov/InMemoryBucket), an in-process S3 fake for .NET tests: buckets, objects, multipart uploads, ETags and checksums. It has no SDK dependency.

You rarely reference this package directly. It comes with `InMemoryBucket`, and its types (`ObjectStore`, `Bucket`, `S3Object`, `S3ErrorCode`) show up through `server.Store`, `server.GetObject()` and fault rules.

```csharp
using var s3 = new InMemoryBucketServer(o => o.CreateBuckets("uploads"));
// ... run the code under test ...
var stored = s3.Store.GetBucket("uploads").Get("a/b.png");
Assert.Equal("image/png", stored.Attributes.ContentType);
```

Documentation, supported operations and samples: [https://github.com/saiganov/InMemoryBucket](https://github.com/saiganov/InMemoryBucket).
