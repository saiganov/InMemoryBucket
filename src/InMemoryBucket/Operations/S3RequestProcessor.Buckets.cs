using InMemoryBucket.Core;
using InMemoryBucket.Protocol;

namespace InMemoryBucket.Operations;

internal sealed partial class S3RequestProcessor
{
    private S3Response ListBuckets()
    {
        var buckets = _store.Buckets;
        var body = S3Xml.Document("ListAllMyBucketsResult", w =>
        {
            w.WriteStartElement("Owner");
            w.Element("ID", _options.OwnerId);
            w.Element("DisplayName", _options.OwnerDisplayName);
            w.WriteEndElement();

            w.WriteStartElement("Buckets");
            foreach (var bucket in buckets)
            {
                w.WriteStartElement("Bucket");
                w.Element("Name", bucket.Name);
                w.Element("CreationDate", S3Xml.Iso8601(bucket.CreationDate));
                w.Element("BucketRegion", _options.Region);
                w.WriteEndElement();
            }

            w.WriteEndElement();
        });

        return Xml(200, body);
    }

    private S3Response CreateBucket(S3Request request)
    {
        var created = _store.TryCreateBucket(request.Bucket, out _);
        if (!created && !string.Equals(_options.Region, "us-east-1", StringComparison.OrdinalIgnoreCase))
        {
            // AWS answers 200 for a bucket you already own only in us-east-1; every other region (and MinIO) answers 409.
            throw new S3Exception(S3ErrorCode.BucketAlreadyOwnedByYou, resource: request.Resource);
        }

        return new S3Response { StatusCode = 200 }.WithHeader("Location", "/" + request.Bucket);
    }

    private S3Response HeadBucket(S3Request request)
    {
        RequireBucket(request);
        return new S3Response { StatusCode = 200 }
            .WithHeader("x-amz-bucket-region", _options.Region)
            .WithHeader("x-amz-access-point-alias", "false");
    }

    private S3Response DeleteBucket(S3Request request)
    {
        _store.DeleteBucket(request.Bucket);
        return NoContent();
    }

    private S3Response GetBucketLocation(S3Request request)
    {
        RequireBucket(request);
        var region = string.Equals(_options.Region, "us-east-1", StringComparison.OrdinalIgnoreCase) ? null : _options.Region;
        var body = S3Xml.Document("LocationConstraint", w =>
        {
            if (region is not null)
            {
                w.WriteString(region);
            }
        });

        return Xml(200, body);
    }

    private S3Response GetBucketAcl(S3Request request)
    {
        RequireBucket(request);
        var body = S3Xml.Document("AccessControlPolicy", w =>
        {
            w.WriteStartElement("Owner");
            w.Element("ID", _options.OwnerId);
            w.Element("DisplayName", _options.OwnerDisplayName);
            w.WriteEndElement();

            w.WriteStartElement("AccessControlList");
            w.WriteStartElement("Grant");
            w.WriteStartElement("Grantee");
            w.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
            w.WriteAttributeString("xsi", "type", "http://www.w3.org/2001/XMLSchema-instance", "CanonicalUser");
            w.Element("ID", _options.OwnerId);
            w.Element("DisplayName", _options.OwnerDisplayName);
            w.WriteEndElement();
            w.Element("Permission", "FULL_CONTROL");
            w.WriteEndElement();
            w.WriteEndElement();
        });

        return Xml(200, body);
    }

    /// <summary>ListObjects (v1): marker paging instead of continuation tokens, Owner always present. The MinIO SDK can be told to use it.</summary>
    private S3Response ListObjects(S3Request request)
    {
        var bucket = RequireBucket(request);
        var maxKeys = ParseMaxKeys(request);
        var marker = request.QueryValue("marker");
        var query = new ListObjectsV2Query(
            Prefix: request.QueryValue("prefix") ?? string.Empty,
            Delimiter: request.QueryValue("delimiter"),
            MaxKeys: maxKeys,
            StartAfter: string.IsNullOrEmpty(marker) ? null : marker);
        var urlEncode = string.Equals(request.QueryValue("encoding-type"), "url", StringComparison.OrdinalIgnoreCase);
        string Enc(string value) => urlEncode ? S3UrlEncode(value) : value;

        var result = bucket.ListObjectsV2(query);
        var lastEmitted = result.Contents.Count > 0 || result.CommonPrefixes.Count > 0
            ? new[] { result.Contents.LastOrDefault()?.Key, result.CommonPrefixes.LastOrDefault() }.Where(k => k is not null).Max(StringComparer.Ordinal)
            : null;

        var body = S3Xml.Document("ListBucketResult", w =>
        {
            w.Element("Name", bucket.Name);
            w.Element("Prefix", Enc(query.Prefix));
            w.Element("Marker", marker is null ? string.Empty : Enc(marker));
            if (result.IsTruncated && lastEmitted is not null) w.Element("NextMarker", Enc(lastEmitted));
            w.Element("MaxKeys", Math.Clamp(query.MaxKeys, 0, 1000));
            if (query.Delimiter is not null) w.Element("Delimiter", Enc(query.Delimiter));
            if (urlEncode) w.Element("EncodingType", "url");
            w.Element("IsTruncated", result.IsTruncated);

            foreach (var obj in result.Contents)
            {
                w.WriteStartElement("Contents");
                w.Element("Key", Enc(obj.Key));
                w.Element("LastModified", S3Xml.Iso8601(obj.LastModified));
                w.Element("ETag", obj.ETag);
                w.Element("Size", obj.Size);
                w.Element("StorageClass", obj.Attributes.StorageClass);
                WriteOwner(w, "Owner");
                w.WriteEndElement();
            }

            foreach (var prefix in result.CommonPrefixes)
            {
                w.WriteStartElement("CommonPrefixes");
                w.Element("Prefix", Enc(prefix));
                w.WriteEndElement();
            }
        });

        return Xml(200, body);
    }

    private static int ParseMaxKeys(S3Request request)
    {
        var maxKeys = 1000;
        if (request.QueryValue("max-keys") is { } maxKeysText && !int.TryParse(maxKeysText, out maxKeys))
        {
            throw new S3Exception(S3ErrorCode.InvalidArgument, "Argument max-keys must be an integer between 0 and 2147483647");
        }

        return maxKeys;
    }

    private S3Response ListObjectsV2(S3Request request)
    {
        var bucket = RequireBucket(request);
        var maxKeys = ParseMaxKeys(request);

        var query = new ListObjectsV2Query(
            Prefix: request.QueryValue("prefix") ?? string.Empty,
            Delimiter: request.QueryValue("delimiter"),
            MaxKeys: maxKeys,
            ContinuationToken: request.QueryValue("continuation-token"),
            StartAfter: request.QueryValue("start-after"));
        var urlEncode = string.Equals(request.QueryValue("encoding-type"), "url", StringComparison.OrdinalIgnoreCase);
        var fetchOwner = string.Equals(request.QueryValue("fetch-owner"), "true", StringComparison.OrdinalIgnoreCase);
        string Enc(string value) => urlEncode ? S3UrlEncode(value) : value;

        var result = bucket.ListObjectsV2(query);
        var body = S3Xml.Document("ListBucketResult", w =>
        {
            w.Element("Name", bucket.Name);
            w.Element("Prefix", Enc(query.Prefix));
            if (query.StartAfter is not null) w.Element("StartAfter", Enc(query.StartAfter));
            if (query.ContinuationToken is not null) w.Element("ContinuationToken", query.ContinuationToken);
            if (result.NextContinuationToken is not null) w.Element("NextContinuationToken", result.NextContinuationToken);
            w.Element("KeyCount", result.KeyCount);
            w.Element("MaxKeys", Math.Clamp(query.MaxKeys, 0, 1000));
            if (query.Delimiter is not null) w.Element("Delimiter", Enc(query.Delimiter));
            if (urlEncode) w.Element("EncodingType", "url");
            w.Element("IsTruncated", result.IsTruncated);

            foreach (var obj in result.Contents)
            {
                w.WriteStartElement("Contents");
                w.Element("Key", Enc(obj.Key));
                w.Element("LastModified", S3Xml.Iso8601(obj.LastModified));
                w.Element("ETag", obj.ETag);
                w.Element("Size", obj.Size);
                w.Element("StorageClass", obj.Attributes.StorageClass);
                if (fetchOwner)
                {
                    w.WriteStartElement("Owner");
                    w.Element("ID", _options.OwnerId);
                    w.Element("DisplayName", _options.OwnerDisplayName);
                    w.WriteEndElement();
                }

                w.WriteEndElement();
            }

            foreach (var prefix in result.CommonPrefixes)
            {
                w.WriteStartElement("CommonPrefixes");
                w.Element("Prefix", Enc(prefix));
                w.WriteEndElement();
            }
        });

        return Xml(200, body);
    }
}
