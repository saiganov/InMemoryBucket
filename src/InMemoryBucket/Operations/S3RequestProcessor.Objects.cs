using System.Globalization;
using InMemoryBucket.Core;
using InMemoryBucket.Protocol;

namespace InMemoryBucket.Operations;

internal sealed partial class S3RequestProcessor
{
    private static readonly (string Query, string Header)[] ResponseOverrides =
    [
        ("response-content-type", "Content-Type"),
        ("response-content-language", "Content-Language"),
        ("response-expires", "Expires"),
        ("response-cache-control", "Cache-Control"),
        ("response-content-disposition", "Content-Disposition"),
        ("response-content-encoding", "Content-Encoding"),
    ];

    private S3Response PutObject(S3Request request)
    {
        var bucket = RequireBucket(request);
        VerifyUploadIntegrity(request, request.Body.Span);

        var obj = bucket.Put(request.Key, request.Body, ReadAttributes(request), request.DeclaredChecksums, condition: WriteConditionOf(request));

        var response = new S3Response { StatusCode = 200 }.WithHeader("ETag", obj.ETag);
        AddChecksumHeaders(response, request.DeclaredChecksums.IsEmpty ? ChecksumSet.Empty : obj.Checksums, obj.ChecksumType);
        AddEncryptionHeaders(response, obj.Attributes.Encryption);
        return response;
    }

    private static WriteCondition WriteConditionOf(S3Request request)
        => new(request.Header("If-Match"), request.Header("If-None-Match"));

    private S3Response GetObject(S3Request request, bool includeBody)
    {
        var bucket = RequireBucket(request);
        var obj = bucket.Get(request.Key);

        CheckPreconditions(request, obj);

        S3Response response;
        if (request.Header("Range") is { } rangeHeader)
        {
            var (from, to) = ParseRange(rangeHeader, obj.Size);
            var slice = obj.Data.Slice((int)from, (int)(to - from + 1));
            response = new S3Response { StatusCode = 206, DeclaredContentLength = slice.Length, Body = includeBody ? slice : default }
                .WithHeader("Content-Range", $"bytes {from}-{to}/{obj.Size}");
        }
        else
        {
            response = new S3Response { StatusCode = 200, DeclaredContentLength = obj.Size, Body = includeBody ? obj.Data : default };
            if (string.Equals(request.Header("x-amz-checksum-mode"), "ENABLED", StringComparison.OrdinalIgnoreCase))
            {
                AddChecksumHeaders(response, obj.Checksums, obj.ChecksumType);
            }
        }

        CopyHeaders(response, obj, request);
        return response;
    }

    private static void CopyHeaders(S3Response response, S3Object obj, S3Request request)
    {
        response.Headers["ETag"] = obj.ETag;
        response.Headers["Last-Modified"] = S3Xml.Rfc1123(obj.LastModified);
        response.Headers["Accept-Ranges"] = "bytes";
        response.Headers["Content-Type"] = obj.Attributes.ContentType;
        AddEncryptionHeaders(response, obj.Attributes.Encryption);
        foreach (var (name, value) in obj.Attributes.Headers)
        {
            response.Headers[name] = value;
        }

        foreach (var (name, value) in obj.Attributes.UserMetadata)
        {
            response.Headers["x-amz-meta-" + name] = value;
        }

        if (obj.Attributes.Tags.Count > 0)
        {
            response.Headers["x-amz-tagging-count"] = obj.Attributes.Tags.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (obj.Attributes.StorageClass != "STANDARD")
        {
            response.Headers["x-amz-storage-class"] = obj.Attributes.StorageClass;
        }

        foreach (var (query, header) in ResponseOverrides)
        {
            if (request.QueryValue(query) is { } value)
            {
                response.Headers[header] = value;
            }
        }
    }

    private S3Response DeleteObject(S3Request request)
    {
        var bucket = RequireBucket(request);
        bucket.Delete(request.Key);
        return NoContent();
    }

    private S3Response CopyObject(S3Request request)
    {
        var target = RequireBucket(request);
        var (sourceBucketName, sourceKey) = ParseCopySource(request.Header("x-amz-copy-source")!);
        var sourceBucket = _store.GetBucket(sourceBucketName);
        var source = sourceBucket.Get(sourceKey);

        CheckPreconditions(
            source,
            request.Header("x-amz-copy-source-if-match"),
            request.Header("x-amz-copy-source-if-none-match"),
            request.Header("x-amz-copy-source-if-modified-since"),
            request.Header("x-amz-copy-source-if-unmodified-since"),
            S3ErrorCode.PreconditionFailed);

        var replaceMetadata = string.Equals(request.Header("x-amz-metadata-directive"), "REPLACE", StringComparison.OrdinalIgnoreCase);
        var storageClass = request.Header("x-amz-storage-class");
        var sameObject = ReferenceEquals(sourceBucket, target) && sourceKey == request.Key;
        var encryptionChange = request.Header("x-amz-server-side-encryption") is not null;
        if (sameObject && !replaceMetadata && !encryptionChange && (storageClass is null || storageClass == source.Attributes.StorageClass))
        {
            throw new S3Exception(
                S3ErrorCode.InvalidRequest,
                "This copy request is illegal because it is trying to copy an object to itself without changing the object's metadata, storage class, website redirect location or encryption attributes.");
        }

        var attributes = replaceMetadata
            ? ReadAttributes(request)
            : source.Attributes with { StorageClass = storageClass ?? source.Attributes.StorageClass };

        var taggingDirective = request.Header("x-amz-tagging-directive")?.ToUpperInvariant() ?? "COPY";
        attributes = taggingDirective switch
        {
            "COPY" => attributes with { Tags = source.Attributes.Tags },
            "REPLACE" => attributes with { Tags = ObjectTags.ParseHeader(request.Header("x-amz-tagging")) },
            _ => throw new S3Exception(S3ErrorCode.InvalidArgument, "Unknown tagging directive."),
        };

        // The copy is encrypted as this request asks (or with SSE-S3), not as the source was.
        attributes = attributes with { Encryption = ReadEncryption(request) };

        // A copy is a fresh single-part object: new ETag, full-object checksums. The client may ask for a new algorithm.
        var checksums = ChecksumSet.Empty;
        if (request.Header("x-amz-checksum-algorithm") is { } algorithmName)
        {
            if (!Checksums.TryParse(algorithmName, out var algorithm))
            {
                throw new S3Exception(S3ErrorCode.InvalidArgument, $"Checksum algorithm '{algorithmName}' is not supported.");
            }

            checksums = checksums.With(algorithm, Checksums.Compute(algorithm, source.Data.Span));
        }
        else
        {
            foreach (var (algorithm, _) in source.Checksums.Entries())
            {
                if (Checksums.CanCompute(algorithm))
                {
                    checksums = checksums.With(algorithm, Checksums.Compute(algorithm, source.Data.Span));
                }
            }
        }

        var copy = target.Put(request.Key, source.Data, attributes, checksums, checksums.IsEmpty ? null : "FULL_OBJECT");

        var body = S3Xml.Document("CopyObjectResult", w =>
        {
            w.Element("LastModified", S3Xml.Iso8601(copy.LastModified));
            w.Element("ETag", copy.ETag);
            foreach (var (algorithm, value) in copy.Checksums.Entries())
            {
                w.Element("Checksum" + Checksums.AwsName(algorithm), value);
            }

            if (copy.ChecksumType is not null)
            {
                w.Element("ChecksumType", copy.ChecksumType);
            }
        });

        var response = Xml(200, body);
        AddEncryptionHeaders(response, copy.Attributes.Encryption);
        return response;
    }

    /// <summary><c>/bucket/key</c> or <c>bucket/key</c>, URL-encoded, with an optional <c>?versionId=</c> that we accept and ignore.</summary>
    private static (string Bucket, string Key) ParseCopySource(string header)
    {
        var value = header;
        var question = value.IndexOf('?');
        if (question >= 0)
        {
            value = value[..question];
        }

        value = Uri.UnescapeDataString(value).TrimStart('/');
        var slash = value.IndexOf('/');
        if (slash <= 0 || slash == value.Length - 1)
        {
            throw new S3Exception(S3ErrorCode.InvalidArgument, "Copy Source must mention the source bucket and key: sourcebucket/sourcekey");
        }

        return (value[..slash], value[(slash + 1)..]);
    }

    private S3Response DeleteObjects(S3Request request)
    {
        var bucket = RequireBucket(request);
        VerifyUploadIntegrity(request, request.Body.Span);

        var root = ParseXmlBody(request.Body, "Delete");
        var quiet = string.Equals(ChildValue(root, "Quiet"), "true", StringComparison.OrdinalIgnoreCase);
        var keys = new List<string>();
        foreach (var element in root.Elements().Where(e => e.Name.LocalName == "Object"))
        {
            if (ChildValue(element, "Key") is not { Length: > 0 } key)
            {
                throw new S3Exception(S3ErrorCode.MalformedXML);
            }

            keys.Add(key);
        }

        if (keys.Count is 0 or > 1000)
        {
            throw new S3Exception(S3ErrorCode.MalformedXML);
        }

        // S3 reports a key as deleted whether or not it existed.
        foreach (var key in keys)
        {
            bucket.Delete(key);
        }

        var body = S3Xml.Document("DeleteResult", w =>
        {
            if (quiet)
            {
                return;
            }

            foreach (var key in keys)
            {
                w.WriteStartElement("Deleted");
                w.Element("Key", key);
                w.WriteEndElement();
            }
        });

        return Xml(200, body);
    }

    private static void CheckPreconditions(S3Request request, S3Object obj)
        => CheckPreconditions(
            obj,
            request.Header("If-Match"),
            request.Header("If-None-Match"),
            request.Header("If-Modified-Since"),
            request.Header("If-Unmodified-Since"),
            S3ErrorCode.NotModified);

    /// <summary>
    /// Order follows RFC 7232 as S3 applies it: If-Match / If-Unmodified-Since fail with 412, then If-None-Match / If-Modified-Since
    /// fail with <paramref name="notModifiedCode"/>: 304 for GET and HEAD, 412 for the x-amz-copy-source-* variants.
    /// </summary>
    private static void CheckPreconditions(S3Object obj, string? ifMatch, string? ifNoneMatch, string? ifModifiedSince, string? ifUnmodifiedSince, S3ErrorCode notModifiedCode)
    {
        if (ifMatch is not null && ifMatch != "*" && !ifMatch.Split(',').Any(t => ETags.AreEqual(t.Trim(), obj.ETag)))
        {
            throw new S3Exception(S3ErrorCode.PreconditionFailed);
        }

        if (ifUnmodifiedSince is not null
            && TryParseHttpDate(ifUnmodifiedSince, out var unmodifiedSince)
            && Truncate(obj.LastModified) > unmodifiedSince)
        {
            throw new S3Exception(S3ErrorCode.PreconditionFailed);
        }

        if (ifNoneMatch is not null
            && (ifNoneMatch == "*" || ifNoneMatch.Split(',').Any(t => ETags.AreEqual(t.Trim(), obj.ETag))))
        {
            throw new S3Exception(notModifiedCode);
        }

        if (ifModifiedSince is not null
            && TryParseHttpDate(ifModifiedSince, out var modifiedSince)
            && Truncate(obj.LastModified) <= modifiedSince)
        {
            throw new S3Exception(notModifiedCode);
        }
    }

    private static DateTimeOffset Truncate(DateTimeOffset value)
        => new(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, value.Offset);

    private static bool TryParseHttpDate(string value, out DateTimeOffset result)
        => DateTimeOffset.TryParseExact(value, "R", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out result)
           || DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out result);

    private static (long From, long To) ParseRange(string header, long size)
    {
        // bytes=0-99 | bytes=100- | bytes=-50
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || header.Contains(','))
        {
            throw new S3Exception(S3ErrorCode.InvalidRange);
        }

        var spec = header["bytes=".Length..];
        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            throw new S3Exception(S3ErrorCode.InvalidRange);
        }

        var fromText = spec[..dash];
        var toText = spec[(dash + 1)..];

        long from;
        long to;
        if (fromText.Length == 0)
        {
            if (!long.TryParse(toText, out var suffix) || suffix <= 0)
            {
                throw new S3Exception(S3ErrorCode.InvalidRange);
            }

            from = Math.Max(0, size - suffix);
            to = size - 1;
        }
        else
        {
            if (!long.TryParse(fromText, out from) || from < 0)
            {
                throw new S3Exception(S3ErrorCode.InvalidRange);
            }

            to = toText.Length == 0 ? size - 1 : long.TryParse(toText, out var t) ? Math.Min(t, size - 1) : throw new S3Exception(S3ErrorCode.InvalidRange);
        }

        if (size == 0 || from >= size || to < from)
        {
            throw new S3Exception(S3ErrorCode.InvalidRange);
        }

        return (from, to);
    }
}
