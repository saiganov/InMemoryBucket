using InMemoryBucket.Core;
using InMemoryBucket.Protocol;

namespace InMemoryBucket.Operations;

internal sealed partial class S3RequestProcessor
{
    private S3Response GetObjectTagging(S3Request request)
    {
        var obj = RequireBucket(request).Get(request.Key);
        var body = S3Xml.Document("Tagging", w =>
        {
            w.WriteStartElement("TagSet");
            foreach (var (key, value) in obj.Attributes.Tags.OrderBy(t => t.Key, StringComparer.Ordinal))
            {
                w.WriteStartElement("Tag");
                w.Element("Key", key);
                w.Element("Value", value);
                w.WriteEndElement();
            }

            w.WriteEndElement();
        });

        return Xml(200, body);
    }

    private S3Response PutObjectTagging(S3Request request)
    {
        var bucket = RequireBucket(request);
        VerifyUploadIntegrity(request, request.Body.Span);

        var root = ParseXmlBody(request.Body, "Tagging", preserveWhitespace: true);
        var tagSet = root.Elements().FirstOrDefault(e => e.Name.LocalName == "TagSet") ?? throw new S3Exception(S3ErrorCode.MalformedXML);
        var tags = ObjectTags.Create(tagSet.Elements()
            .Where(e => e.Name.LocalName == "Tag")
            .Select(e => KeyValuePair.Create(
                ChildValue(e, "Key") ?? throw new S3Exception(S3ErrorCode.MalformedXML),
                ChildValue(e, "Value") ?? throw new S3Exception(S3ErrorCode.MalformedXML))));

        bucket.SetTags(request.Key, tags);
        return new S3Response { StatusCode = 200 };
    }

    private S3Response DeleteObjectTagging(S3Request request)
    {
        RequireBucket(request).SetTags(request.Key, ObjectTags.Empty);
        return NoContent();
    }
}
