using System.Globalization;
using System.Text;
using System.Xml;

namespace InMemoryBucket.Protocol;

internal static class S3Xml
{
    public const string Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";

    private static readonly XmlWriterSettings Settings = new()
    {
        Encoding = new UTF8Encoding(false),
        OmitXmlDeclaration = false,
        Indent = false,
    };

    /// <summary>Writes a root element in the S3 namespace and lets <paramref name="body"/> fill it.</summary>
    public static byte[] Document(string root, Action<XmlWriter> body, bool inS3Namespace = true)
    {
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, Settings))
        {
            writer.WriteStartDocument();
            if (inS3Namespace)
            {
                writer.WriteStartElement(root, Namespace);
            }
            else
            {
                writer.WriteStartElement(root);
            }

            body(writer);
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return stream.ToArray();
    }

    public static void Element(this XmlWriter writer, string name, string? value)
    {
        writer.WriteStartElement(name);
        if (value is not null)
        {
            writer.WriteString(value);
        }

        writer.WriteEndElement();
    }

    public static void Element(this XmlWriter writer, string name, long value)
        => writer.Element(name, value.ToString(CultureInfo.InvariantCulture));

    public static void Element(this XmlWriter writer, string name, bool value)
        => writer.Element(name, value ? "true" : "false");

    public static string Iso8601(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static string Rfc1123(DateTimeOffset value)
        => value.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);
}
