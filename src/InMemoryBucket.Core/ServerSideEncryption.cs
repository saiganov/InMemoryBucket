namespace InMemoryBucket.Core;

/// <summary>
/// The server-side encryption S3 reports for an object. Nothing is encrypted in memory; the settings are stored and echoed
/// so code that checks <c>x-amz-server-side-encryption</c> sees what S3 would answer. S3 encrypts every object with SSE-S3
/// (<c>AES256</c>) unless the request asks for KMS.
/// </summary>
/// <param name="Algorithm">The value of <c>x-amz-server-side-encryption</c>.</param>
/// <param name="KmsKeyId">The KMS key id for KMS encryption, or null.</param>
/// <param name="BucketKeyEnabled">Whether an S3 Bucket Key is used with KMS.</param>
public sealed record ServerSideEncryption(string Algorithm, string? KmsKeyId = null, bool BucketKeyEnabled = false)
{
    /// <summary>SSE-S3, S3's default.</summary>
    public const string Aes256 = "AES256";
    /// <summary>SSE-KMS.</summary>
    public const string Kms = "aws:kms";
    /// <summary>Dual-layer SSE-KMS.</summary>
    public const string KmsDsse = "aws:kms:dsse";

    /// <summary>SSE-S3 (<c>AES256</c>), what S3 reports when the request does not ask for encryption.</summary>
    public static readonly ServerSideEncryption Default = new(Aes256);

    /// <summary>True for <see cref="Kms"/> and <see cref="KmsDsse"/>.</summary>
    public bool IsKms => Algorithm is Kms or KmsDsse;

    /// <summary>Parses the request headers as S3 validates them: unknown algorithms and a KMS key id without KMS are InvalidArgument.</summary>
    internal static ServerSideEncryption Parse(string? algorithm, string? kmsKeyId, string? bucketKeyEnabled)
    {
        if (algorithm is null)
        {
            if (kmsKeyId is not null)
            {
                throw new S3Exception(S3ErrorCode.InvalidArgument, "Specifying a KMS key id requires x-amz-server-side-encryption: aws:kms.");
            }

            return Default;
        }

        if (algorithm is not (Aes256 or Kms or KmsDsse))
        {
            throw new S3Exception(S3ErrorCode.InvalidArgument, $"The encryption method specified is not supported: {algorithm}");
        }

        if (kmsKeyId is not null && algorithm == Aes256)
        {
            throw new S3Exception(S3ErrorCode.InvalidArgument, "Specifying a KMS key id requires x-amz-server-side-encryption: aws:kms.");
        }

        return new ServerSideEncryption(
            algorithm,
            kmsKeyId,
            algorithm != Aes256 && string.Equals(bucketKeyEnabled, "true", StringComparison.OrdinalIgnoreCase));
    }
}
