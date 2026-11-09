namespace InMemoryBucket.Core;

/// <summary>
/// An S3 error code with the HTTP status S3 returns for it and the message S3 uses by default.
/// The static fields are the codes the fake answers with; pass one to a fault rule to make it answer like S3.
/// </summary>
/// <param name="Code">The value of the <c>&lt;Code&gt;</c> element, for example <c>NoSuchKey</c>.</param>
/// <param name="HttpStatus">The HTTP status S3 returns with this code.</param>
/// <param name="DefaultMessage">The <c>&lt;Message&gt;</c> used when the error does not carry its own.</param>
public sealed record S3ErrorCode(string Code, int HttpStatus, string DefaultMessage)
{
    /// <summary><c>NoSuchBucket</c>, HTTP 404.</summary>
    public static readonly S3ErrorCode NoSuchBucket = new("NoSuchBucket", 404, "The specified bucket does not exist");
    /// <summary><c>NoSuchKey</c>, HTTP 404.</summary>
    public static readonly S3ErrorCode NoSuchKey = new("NoSuchKey", 404, "The specified key does not exist.");
    /// <summary><c>NoSuchUpload</c>, HTTP 404.</summary>
    public static readonly S3ErrorCode NoSuchUpload = new("NoSuchUpload", 404, "The specified multipart upload does not exist. The upload ID might be invalid, or the multipart upload might have been aborted or completed.");
    /// <summary><c>BucketAlreadyOwnedByYou</c>, HTTP 409.</summary>
    public static readonly S3ErrorCode BucketAlreadyOwnedByYou = new("BucketAlreadyOwnedByYou", 409, "Your previous request to create the named bucket succeeded and you already own it.");
    /// <summary><c>BucketNotEmpty</c>, HTTP 409.</summary>
    public static readonly S3ErrorCode BucketNotEmpty = new("BucketNotEmpty", 409, "The bucket you tried to delete is not empty");
    /// <summary><c>InvalidBucketName</c>, HTTP 400.</summary>
    public static readonly S3ErrorCode InvalidBucketName = new("InvalidBucketName", 400, "The specified bucket is not valid.");
    /// <summary><c>InvalidPart</c>, HTTP 400.</summary>
    public static readonly S3ErrorCode InvalidPart = new("InvalidPart", 400, "One or more of the specified parts could not be found. The part might not have been uploaded, or the specified entity tag might not have matched the part's entity tag.");
    /// <summary><c>InvalidPartOrder</c>, HTTP 400.</summary>
    public static readonly S3ErrorCode InvalidPartOrder = new("InvalidPartOrder", 400, "The list of parts was not in ascending order. The parts list must be specified in order by part number.");
    /// <summary><c>EntityTooSmall</c>, HTTP 400.</summary>
    public static readonly S3ErrorCode EntityTooSmall = new("EntityTooSmall", 400, "Your proposed upload is smaller than the minimum allowed object size. Each part must be at least 5 MB in size, except the last part.");
    /// <summary><c>BadDigest</c>, HTTP 400.</summary>
    public static readonly S3ErrorCode BadDigest = new("BadDigest", 400, "The Content-MD5 or checksum value that you specified did not match what the server received.");
    /// <summary><c>InvalidDigest</c>, HTTP 400.</summary>
    public static readonly S3ErrorCode InvalidDigest = new("InvalidDigest", 400, "The Content-MD5 or checksum value that you specified is not valid.");
    /// <summary><c>MalformedXML</c>, HTTP 400.</summary>
    public static readonly S3ErrorCode MalformedXML = new("MalformedXML", 400, "The XML you provided was not well-formed or did not validate against our published schema.");
    /// <summary><c>InvalidArgument</c>, HTTP 400.</summary>
    public static readonly S3ErrorCode InvalidArgument = new("InvalidArgument", 400, "Invalid Argument");
    /// <summary><c>InvalidTag</c>, HTTP 400.</summary>
    public static readonly S3ErrorCode InvalidTag = new("InvalidTag", 400, "The tag provided was not a valid tag.");
    /// <summary><c>KeyTooLongError</c>, HTTP 400.</summary>
    public static readonly S3ErrorCode KeyTooLongError = new("KeyTooLongError", 400, "Your key is too long");
    /// <summary><c>MetadataTooLarge</c>, HTTP 400.</summary>
    public static readonly S3ErrorCode MetadataTooLarge = new("MetadataTooLarge", 400, "Your metadata headers exceed the maximum allowed metadata size");
    /// <summary><c>InvalidRequest</c>, HTTP 400.</summary>
    public static readonly S3ErrorCode InvalidRequest = new("InvalidRequest", 400, "Invalid Request");
    /// <summary><c>InvalidRange</c>, HTTP 416.</summary>
    public static readonly S3ErrorCode InvalidRange = new("InvalidRange", 416, "The requested range is not satisfiable");
    /// <summary><c>PreconditionFailed</c>, HTTP 412.</summary>
    public static readonly S3ErrorCode PreconditionFailed = new("PreconditionFailed", 412, "At least one of the pre-conditions you specified did not hold");
    /// <summary><c>NotModified</c>, HTTP 304.</summary>
    public static readonly S3ErrorCode NotModified = new("NotModified", 304, "Not Modified");
    /// <summary><c>MethodNotAllowed</c>, HTTP 405.</summary>
    public static readonly S3ErrorCode MethodNotAllowed = new("MethodNotAllowed", 405, "The specified method is not allowed against this resource.");
    /// <summary><c>NotImplemented</c>, HTTP 501.</summary>
    public static readonly S3ErrorCode NotImplemented = new("NotImplemented", 501, "A header or query you provided implies functionality that is not implemented.");
    /// <summary><c>InternalError</c>, HTTP 500.</summary>
    public static readonly S3ErrorCode InternalError = new("InternalError", 500, "We encountered an internal error. Please try again.");
    /// <summary><c>AccessDenied</c>, HTTP 403.</summary>
    public static readonly S3ErrorCode AccessDenied = new("AccessDenied", 403, "Access Denied");
    /// <summary><c>SignatureDoesNotMatch</c>, HTTP 403.</summary>
    public static readonly S3ErrorCode SignatureDoesNotMatch = new("SignatureDoesNotMatch", 403, "The request signature we calculated does not match the signature you provided. Check your key and signing method.");
    /// <summary><c>InvalidAccessKeyId</c>, HTTP 403.</summary>
    public static readonly S3ErrorCode InvalidAccessKeyId = new("InvalidAccessKeyId", 403, "The AWS Access Key Id you provided does not exist in our records.");
    /// <summary><c>SlowDown</c>, HTTP 503.</summary>
    public static readonly S3ErrorCode SlowDown = new("SlowDown", 503, "Please reduce your request rate.");
    /// <summary><c>ServiceUnavailable</c>, HTTP 503.</summary>
    public static readonly S3ErrorCode ServiceUnavailable = new("ServiceUnavailable", 503, "Service is unable to handle request.");
    /// <summary><c>RequestTimeout</c>, HTTP 400.</summary>
    public static readonly S3ErrorCode RequestTimeout = new("RequestTimeout", 400, "Your socket connection to the server was not read from or written to within the timeout period. Idle connections will be closed.");
    /// <summary><c>RequestTimeTooSkewed</c>, HTTP 403.</summary>
    public static readonly S3ErrorCode RequestTimeTooSkewed = new("RequestTimeTooSkewed", 403, "The difference between the request time and the server's time is too large.");
    /// <summary><c>AuthorizationHeaderMalformed</c>, HTTP 400.</summary>
    public static readonly S3ErrorCode AuthorizationHeaderMalformed = new("AuthorizationHeaderMalformed", 400, "The authorization header is malformed.");
    /// <summary><c>AuthorizationQueryParametersError</c>, HTTP 400.</summary>
    public static readonly S3ErrorCode AuthorizationQueryParametersError = new("AuthorizationQueryParametersError", 400, "Error parsing the X-Amz-Credential parameter; the Credential is mal-formed; expecting \"<YOUR-AKID>/YYYYMMDD/REGION/SERVICE/aws4_request\".");
    /// <summary><c>XAmzContentSHA256Mismatch</c>, HTTP 400.</summary>
    public static readonly S3ErrorCode XAmzContentSHA256Mismatch = new("XAmzContentSHA256Mismatch", 400, "The provided 'x-amz-content-sha256' header does not match what was computed.");
}

/// <summary>
/// Raised by the store or the protocol layer; the HTTP layer turns it into a standard S3 <c>&lt;Error&gt;</c> response.
/// </summary>
public sealed class S3Exception : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code">The S3 error code, which also gives the HTTP status.</param>
    /// <param name="message">The <c>&lt;Message&gt;</c> of the response; <see cref="S3ErrorCode.DefaultMessage"/> when null.</param>
    /// <param name="resource">The <c>&lt;Resource&gt;</c> of the response, for example the bucket or key; omitted when null.</param>
    public S3Exception(S3ErrorCode code, string? message = null, string? resource = null)
        : base(message ?? code.DefaultMessage)
    {
        Code = code;
        Resource = resource;
    }

    /// <summary>The S3 error code.</summary>
    public S3ErrorCode Code { get; }

    /// <summary>The <c>&lt;Resource&gt;</c> of the response, or null.</summary>
    public string? Resource { get; }
}
