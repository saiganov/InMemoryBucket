namespace InMemoryBucket.Protocol;

/// <summary>
/// The S3 operation a request was routed to, as recorded in <see cref="Hooks.RecordedRequest.Operation"/> and matched by fault rules.
/// </summary>
public enum S3Operation
{
    /// <summary>A request the router does not recognise, such as an unsupported sub-resource. It is answered with 501 NotImplemented.</summary>
    Unknown,
    /// <summary><c>GET /</c>: ListBuckets.</summary>
    ListBuckets,
    /// <summary><c>PUT /bucket</c>: CreateBucket.</summary>
    CreateBucket,
    /// <summary><c>HEAD /bucket</c>: HeadBucket.</summary>
    HeadBucket,
    /// <summary><c>DELETE /bucket</c>: DeleteBucket.</summary>
    DeleteBucket,
    /// <summary><c>GET /bucket?location</c>: GetBucketLocation.</summary>
    GetBucketLocation,
    /// <summary><c>GET /bucket?acl</c>: GetBucketAcl.</summary>
    GetBucketAcl,
    /// <summary><c>GET /bucket</c>: ListObjects (v1, markers).</summary>
    ListObjects,
    /// <summary><c>GET /bucket?list-type=2</c>: ListObjectsV2 (continuation tokens).</summary>
    ListObjectsV2,
    /// <summary><c>GET /bucket?uploads</c>: ListMultipartUploads.</summary>
    ListMultipartUploads,
    /// <summary><c>POST /bucket?delete</c>: DeleteObjects (batch delete).</summary>
    DeleteObjects,
    /// <summary><c>PUT /bucket/key</c>: PutObject.</summary>
    PutObject,
    /// <summary><c>PUT /bucket/key</c> with <c>x-amz-copy-source</c>: CopyObject.</summary>
    CopyObject,
    /// <summary><c>GET /bucket/key</c>: GetObject.</summary>
    GetObject,
    /// <summary><c>HEAD /bucket/key</c>: HeadObject.</summary>
    HeadObject,
    /// <summary><c>DELETE /bucket/key</c>: DeleteObject.</summary>
    DeleteObject,
    /// <summary><c>POST /bucket/key?uploads</c>: CreateMultipartUpload.</summary>
    CreateMultipartUpload,
    /// <summary><c>PUT /bucket/key?partNumber=N&amp;uploadId=ID</c>: UploadPart.</summary>
    UploadPart,
    /// <summary>UploadPart with <c>x-amz-copy-source</c>: UploadPartCopy. Recognised but not implemented; answered with 501.</summary>
    UploadPartCopy,
    /// <summary><c>POST /bucket/key?uploadId=ID</c>: CompleteMultipartUpload.</summary>
    CompleteMultipartUpload,
    /// <summary><c>DELETE /bucket/key?uploadId=ID</c>: AbortMultipartUpload.</summary>
    AbortMultipartUpload,
    /// <summary><c>GET /bucket/key?uploadId=ID</c>: ListParts.</summary>
    ListParts,
    /// <summary><c>GET /bucket/key?tagging</c>: GetObjectTagging.</summary>
    GetObjectTagging,
    /// <summary><c>PUT /bucket/key?tagging</c>: PutObjectTagging.</summary>
    PutObjectTagging,
    /// <summary><c>DELETE /bucket/key?tagging</c>: DeleteObjectTagging.</summary>
    DeleteObjectTagging,
}

/// <summary>
/// Maps method + path shape + query markers to an S3 operation. Anything with an unhandled sub-resource is <see cref="S3Operation.Unknown"/>
/// so the caller answers 501 instead of silently treating <c>?versioning</c> as a plain PUT.
/// </summary>
internal static class S3Router
{
    private static readonly HashSet<string> SubResources = new(StringComparer.Ordinal)
    {
        "acl", "accelerate", "analytics", "attributes", "cors", "encryption", "intelligent-tiering", "inventory",
        "legal-hold", "lifecycle", "logging", "metrics", "notification", "object-lock", "ownershipControls", "policy",
        "policyStatus", "publicAccessBlock", "replication", "requestPayment", "restore", "retention", "select",
        "tagging", "torrent", "versioning", "versions", "website",
    };

    public static S3Operation Resolve(S3Request r)
    {
        if (r.Bucket.Length == 0)
        {
            return r.Method == "GET" ? S3Operation.ListBuckets : S3Operation.Unknown;
        }

        if (r.Key.Length == 0 && r.Method == "GET" && r.HasQuery("acl"))
        {
            return S3Operation.GetBucketAcl; // AmazonS3Util.DoesS3BucketExistV2Async probes buckets with GET ?acl.
        }

        if (r.Key.Length > 0 && r.HasQuery("tagging"))
        {
            return r.Method switch
            {
                "GET" => S3Operation.GetObjectTagging,
                "PUT" => S3Operation.PutObjectTagging,
                "DELETE" => S3Operation.DeleteObjectTagging,
                _ => S3Operation.Unknown,
            };
        }

        if (r.Query.Keys.Any(SubResources.Contains))
        {
            return S3Operation.Unknown;
        }

        if (r.Key.Length == 0)
        {
            return r.Method switch
            {
                "GET" when r.HasQuery("location") => S3Operation.GetBucketLocation,
                "GET" when r.HasQuery("uploads") => S3Operation.ListMultipartUploads,
                "GET" when r.QueryValue("list-type") == "2" => S3Operation.ListObjectsV2,
                "GET" => S3Operation.ListObjects,
                "PUT" => S3Operation.CreateBucket,
                "HEAD" => S3Operation.HeadBucket,
                "DELETE" => S3Operation.DeleteBucket,
                "POST" when r.HasQuery("delete") => S3Operation.DeleteObjects,
                _ => S3Operation.Unknown,
            };
        }

        var multipart = r.HasQuery("uploadId");
        var copy = r.Headers.ContainsKey("x-amz-copy-source");
        return r.Method switch
        {
            "PUT" when multipart && r.HasQuery("partNumber") => copy ? S3Operation.UploadPartCopy : S3Operation.UploadPart,
            "PUT" when copy => S3Operation.CopyObject,
            "PUT" => S3Operation.PutObject,
            "GET" when multipart => S3Operation.ListParts,
            "GET" => S3Operation.GetObject,
            "HEAD" => S3Operation.HeadObject,
            "DELETE" when multipart => S3Operation.AbortMultipartUpload,
            "DELETE" => S3Operation.DeleteObject,
            "POST" when r.HasQuery("uploads") => S3Operation.CreateMultipartUpload,
            "POST" when multipart => S3Operation.CompleteMultipartUpload,
            _ => S3Operation.Unknown,
        };
    }
}
