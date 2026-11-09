using System.Globalization;
using InMemoryBucket.Core;
using InMemoryBucket.Protocol;

namespace InMemoryBucket.Operations;

internal sealed partial class S3RequestProcessor
{
    private S3Response CreateMultipartUpload(S3Request request)
    {
        var bucket = RequireBucket(request);

        ChecksumAlgorithm? algorithm = null;
        if (request.Header("x-amz-checksum-algorithm") is { } algorithmName)
        {
            if (!Checksums.TryParse(algorithmName, out var parsed))
            {
                throw new S3Exception(S3ErrorCode.InvalidArgument, $"Checksum algorithm '{algorithmName}' is not supported.");
            }

            algorithm = parsed;
        }

        var checksumType = request.Header("x-amz-checksum-type")?.ToUpperInvariant();
        var upload = bucket.CreateMultipartUpload(request.Key, ReadAttributes(request), algorithm, checksumType);

        var body = S3Xml.Document("InitiateMultipartUploadResult", w =>
        {
            w.Element("Bucket", bucket.Name);
            w.Element("Key", request.Key);
            w.Element("UploadId", upload.UploadId);
        });

        var response = Xml(200, body);
        AddEncryptionHeaders(response, upload.Attributes.Encryption);
        if (algorithm is { } a)
        {
            response.Headers["x-amz-checksum-algorithm"] = Checksums.AwsName(a);
            response.Headers["x-amz-checksum-type"] = checksumType ?? "COMPOSITE";
        }

        return response;
    }

    private S3Response UploadPart(S3Request request)
    {
        var bucket = RequireBucket(request);
        var upload = bucket.GetUpload(request.QueryValue("uploadId")!, request.Key);
        if (!int.TryParse(request.QueryValue("partNumber"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var partNumber))
        {
            throw new S3Exception(S3ErrorCode.InvalidArgument, "Part number must be an integer between 1 and 10000, inclusive");
        }

        VerifyUploadIntegrity(request, request.Body.Span);
        var part = bucket.UploadPart(upload, partNumber, request.Body.ToArray(), request.DeclaredChecksums);

        var response = new S3Response { StatusCode = 200 }.WithHeader("ETag", part.ETag);
        AddChecksumHeaders(response, part.Checksums, checksumType: null);
        AddEncryptionHeaders(response, upload.Attributes.Encryption);
        return response;
    }

    private S3Response CompleteMultipartUpload(S3Request request)
    {
        var bucket = RequireBucket(request);
        var upload = bucket.GetUpload(request.QueryValue("uploadId")!, request.Key);
        var parts = ParseCompletedParts(request.Body);

        var obj = bucket.CompleteMultipartUpload(
            upload,
            parts,
            request.DeclaredChecksums,
            request.Header("x-amz-checksum-type")?.ToUpperInvariant(),
            WriteConditionOf(request));

        var body = S3Xml.Document("CompleteMultipartUploadResult", w =>
        {
            w.Element("Location", $"http://{_options.Host}/{bucket.Name}/{UrlEncodeKey(obj.Key)}");
            w.Element("Bucket", bucket.Name);
            w.Element("Key", obj.Key);
            w.Element("ETag", obj.ETag);
            foreach (var (algorithm, value) in obj.Checksums.Entries())
            {
                w.Element("Checksum" + Checksums.AwsName(algorithm), value);
            }

            if (obj.ChecksumType is not null)
            {
                w.Element("ChecksumType", obj.ChecksumType);
            }
        });

        var response = Xml(200, body).WithHeader("ETag", obj.ETag);
        AddEncryptionHeaders(response, obj.Attributes.Encryption);
        return response;
    }

    private S3Response AbortMultipartUpload(S3Request request)
    {
        var bucket = RequireBucket(request);
        var upload = bucket.GetUpload(request.QueryValue("uploadId")!, request.Key);
        bucket.AbortUpload(upload.UploadId);
        return NoContent();
    }

    private S3Response ListParts(S3Request request)
    {
        var bucket = RequireBucket(request);
        var upload = bucket.GetUpload(request.QueryValue("uploadId")!, request.Key);
        var marker = int.TryParse(request.QueryValue("part-number-marker"), out var m) ? m : 0;
        var maxParts = int.TryParse(request.QueryValue("max-parts"), out var mp) ? Math.Clamp(mp, 0, 1000) : 1000;

        var all = upload.Parts.Values.Where(p => p.PartNumber > marker).OrderBy(p => p.PartNumber).ToList();
        var page = all.Take(maxParts).ToList();
        var truncated = all.Count > page.Count;

        var body = S3Xml.Document("ListPartsResult", w =>
        {
            w.Element("Bucket", bucket.Name);
            w.Element("Key", upload.Key);
            w.Element("UploadId", upload.UploadId);
            w.Element("StorageClass", upload.Attributes.StorageClass);
            w.Element("PartNumberMarker", marker);
            w.Element("NextPartNumberMarker", page.Count > 0 ? page[^1].PartNumber : 0);
            w.Element("MaxParts", maxParts);
            w.Element("IsTruncated", truncated);
            if (upload.ChecksumAlgorithm is { } a)
            {
                w.Element("ChecksumAlgorithm", Checksums.AwsName(a));
                w.Element("ChecksumType", upload.ChecksumType ?? "COMPOSITE");
            }

            foreach (var part in page)
            {
                w.WriteStartElement("Part");
                w.Element("PartNumber", part.PartNumber);
                w.Element("LastModified", S3Xml.Iso8601(part.LastModified));
                w.Element("ETag", part.ETag);
                w.Element("Size", part.Data.Length);
                foreach (var (algorithm, value) in part.Checksums.Entries())
                {
                    w.Element("Checksum" + Checksums.AwsName(algorithm), value);
                }

                w.WriteEndElement();
            }
        });

        return Xml(200, body);
    }

    private S3Response ListMultipartUploads(S3Request request)
    {
        var bucket = RequireBucket(request);
        var maxUploads = 1000;
        if (request.QueryValue("max-uploads") is { } maxText && !int.TryParse(maxText, NumberStyles.Integer, CultureInfo.InvariantCulture, out maxUploads))
        {
            throw new S3Exception(S3ErrorCode.InvalidArgument, "Argument max-uploads must be an integer between 0 and 2147483647");
        }

        var query = new ListMultipartUploadsQuery(
            Prefix: request.QueryValue("prefix") ?? string.Empty,
            Delimiter: request.QueryValue("delimiter"),
            MaxUploads: maxUploads,
            KeyMarker: request.QueryValue("key-marker"),
            UploadIdMarker: request.QueryValue("upload-id-marker"));
        var urlEncode = string.Equals(request.QueryValue("encoding-type"), "url", StringComparison.OrdinalIgnoreCase);
        string Enc(string value) => urlEncode ? S3UrlEncode(value) : value;

        var result = bucket.ListMultipartUploads(query);
        var body = S3Xml.Document("ListMultipartUploadsResult", w =>
        {
            w.Element("Bucket", bucket.Name);
            w.Element("KeyMarker", query.KeyMarker is null ? string.Empty : Enc(query.KeyMarker));
            w.Element("UploadIdMarker", query.UploadIdMarker ?? string.Empty);
            if (result.NextKeyMarker is not null) w.Element("NextKeyMarker", Enc(result.NextKeyMarker));
            if (result.NextUploadIdMarker is not null) w.Element("NextUploadIdMarker", result.NextUploadIdMarker);
            if (query.Delimiter is not null) w.Element("Delimiter", Enc(query.Delimiter));
            w.Element("Prefix", Enc(query.Prefix));
            w.Element("MaxUploads", Math.Clamp(query.MaxUploads, 0, 1000));
            w.Element("IsTruncated", result.IsTruncated);
            if (urlEncode) w.Element("EncodingType", "url");

            foreach (var upload in result.Uploads)
            {
                w.WriteStartElement("Upload");
                w.Element("Key", Enc(upload.Key));
                w.Element("UploadId", upload.UploadId);
                WriteOwner(w, "Initiator");
                WriteOwner(w, "Owner");
                w.Element("StorageClass", upload.Attributes.StorageClass);
                w.Element("Initiated", S3Xml.Iso8601(upload.Initiated));
                if (upload.ChecksumAlgorithm is { } a)
                {
                    w.Element("ChecksumAlgorithm", Checksums.AwsName(a));
                    w.Element("ChecksumType", upload.ChecksumType ?? "COMPOSITE");
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

    private void WriteOwner(System.Xml.XmlWriter w, string element)
    {
        w.WriteStartElement(element);
        w.Element("ID", _options.OwnerId);
        w.Element("DisplayName", _options.OwnerDisplayName);
        w.WriteEndElement();
    }

    private static IReadOnlyList<CompletedPart> ParseCompletedParts(ReadOnlyMemory<byte> body)
    {
        var root = ParseXmlBody(body, "CompleteMultipartUpload");

        var parts = new List<CompletedPart>();
        foreach (var element in root.Elements().Where(e => e.Name.LocalName == "Part"))
        {
            if (!int.TryParse(ChildValue(element, "PartNumber"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var partNumber)
                || ChildValue(element, "ETag") is not { } etag)
            {
                throw new S3Exception(S3ErrorCode.MalformedXML);
            }

            var checksums = ChecksumSet.Empty;
            foreach (var algorithm in Enum.GetValues<ChecksumAlgorithm>())
            {
                if (ChildValue(element, "Checksum" + Checksums.AwsName(algorithm)) is { } checksum)
                {
                    checksums = checksums.With(algorithm, checksum);
                }
            }

            parts.Add(new CompletedPart(partNumber, etag, checksums));
        }

        return parts;
    }
}
