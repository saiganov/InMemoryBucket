using System.Net;

namespace InMemoryBucket.Core;

/// <summary>General-purpose bucket naming rules: 3–63 chars, lowercase letters, digits, dots and hyphens, no IP addresses.</summary>
internal static class BucketNames
{
    public static bool IsValid(string name)
    {
        if (name.Length is < 3 or > 63)
        {
            return false;
        }

        if (!char.IsAsciiLetterLower(name[0]) && !char.IsAsciiDigit(name[0]))
        {
            return false;
        }

        if (!char.IsAsciiLetterLower(name[^1]) && !char.IsAsciiDigit(name[^1]))
        {
            return false;
        }

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-'))
            {
                return false;
            }

            if (c == '.' && i > 0 && name[i - 1] == '.')
            {
                return false;
            }
        }

        return !IPAddress.TryParse(name, out _);
    }

    public static void EnsureValid(string name)
    {
        if (!IsValid(name))
        {
            throw new S3Exception(S3ErrorCode.InvalidBucketName, resource: "/" + name);
        }
    }
}
