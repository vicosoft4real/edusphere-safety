namespace EduSphere.Safety.Infrastructure;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>"File" (dev default) or "S3" (S3-compatible, e.g. MinIO).</summary>
    public string Backend { get; set; } = "File";

    public string Endpoint { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string Region { get; set; } = "us-east-1";
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public bool ForcePathStyle { get; set; } = true;

    public string FileRoot { get; set; } = string.Empty;

    /// <summary>HMAC secret for File-backend presigned tokens. Never a guessable value.</summary>
    public string TokenSecret { get; set; } = string.Empty;

    /// <summary>
    /// Fail-fast validation, mirroring the Auth options check: missing or weak
    /// storage configuration throws at startup instead of failing uploads later.
    /// Presigned-URL expiry validation elsewhere allows a 5-minute grace for
    /// edge/cloud clock skew; document wider drift as an operations issue.
    /// </summary>
    public static void Validate(StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.Equals(options.Backend, "File", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.Backend, "S3", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Storage:Backend must be 'File' or 'S3'.");
        if (string.Equals(options.Backend, "File", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(options.FileRoot))
                throw new InvalidOperationException(
                    "Storage:FileRoot is required when Storage:Backend is 'File'.");
            if (System.Text.Encoding.UTF8.GetByteCount(options.TokenSecret) < 32)
                throw new InvalidOperationException(
                    "Storage:TokenSecret must be at least 32 bytes (UTF-8) when Storage:Backend is 'File'.");
            return;
        }

        if (string.IsNullOrWhiteSpace(options.Endpoint))
            throw new InvalidOperationException("Storage:Endpoint is required when Storage:Backend is 'S3'.");
        if (string.IsNullOrWhiteSpace(options.Bucket))
            throw new InvalidOperationException("Storage:Bucket is required when Storage:Backend is 'S3'.");
        if (string.IsNullOrWhiteSpace(options.AccessKey))
            throw new InvalidOperationException("Storage:AccessKey is required when Storage:Backend is 'S3'.");
        if (string.IsNullOrWhiteSpace(options.SecretKey))
            throw new InvalidOperationException("Storage:SecretKey is required when Storage:Backend is 'S3'.");
    }
}
