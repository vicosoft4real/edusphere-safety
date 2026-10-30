using System.Security.Cryptography;
using System.Text;
using EduSphere.Safety.Application;

namespace EduSphere.Safety.Infrastructure;

/// <summary>
/// Development/single-node evidence backend: bytes live under
/// <c>Storage:FileRoot</c>, and presigned URLs are Api-relative tokens
/// (HMAC-SHA256 over tenant, purpose, key, expiry) served by the mediated
/// upload/download endpoints. Upload and download tokens never interchange.
/// Expiry validation allows a 5-minute grace for edge/cloud clock skew.
/// </summary>
public sealed class FileEvidenceStore : IEvidenceStore
{
    public const string UploadPurpose = "upload";
    public const string DownloadPurpose = "download";

    /// <summary>Clock-skew grace applied when validating token expiry.</summary>
    public static readonly TimeSpan ExpiryGrace = TimeSpan.FromMinutes(5);

    private readonly EvidenceManifestRepository _manifests;
    private readonly StorageOptions _storage;

    public FileEvidenceStore(PostgresOptions postgres, StorageOptions storage)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        ArgumentNullException.ThrowIfNull(storage);
        _manifests = new EvidenceManifestRepository(postgres);
        _storage = storage;
    }

    public Task RegisterManifestAsync(EvidenceManifest manifest, CancellationToken ct) =>
        _manifests.RegisterAsync(manifest, ct);

    public Task<EvidenceManifest?> FindManifestAsync(string tenantId, string edgeEventId, string kind, CancellationToken ct) =>
        _manifests.FindAsync(tenantId, edgeEventId, kind, ct);

    public Task<bool> MarkUploadedAsync(string tenantId, string edgeEventId, string kind, CancellationToken ct) =>
        _manifests.MarkUploadedAsync(tenantId, edgeEventId, kind, ct);

    public Task LogAccessAsync(AccessLogRow row, CancellationToken ct) =>
        _manifests.LogAccessAsync(row, ct);

    public Task<IReadOnlyList<AccessLogRow>> ListAccessLogAsync(string tenantId, CancellationToken ct) =>
        _manifests.ListAccessLogAsync(tenantId, ct);

    public Task<string> MintPutUrlAsync(string tenantId, string objectKey, DateTimeOffset expiresAtUtc, CancellationToken ct) =>
        Task.FromResult("/v1/evidence/file-upload?token=" + BuildFileToken(tenantId, UploadPurpose, objectKey, expiresAtUtc));

    public Task<string> MintGetUrlAsync(string tenantId, string objectKey, DateTimeOffset expiresAtUtc, CancellationToken ct) =>
        Task.FromResult("/v1/evidence/file-download?token=" + BuildFileToken(tenantId, DownloadPurpose, objectKey, expiresAtUtc));

    public Task DeleteAsync(string objectKey, CancellationToken ct)
    {
        var path = ResolvePath(objectKey);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string objectKey, CancellationToken ct) =>
        Task.FromResult(File.Exists(ResolvePath(objectKey)));

    public bool TryValidateFileToken(string urlOrToken, string purpose, out string tenantId, out string objectKey)
    {
        tenantId = string.Empty;
        objectKey = string.Empty;
        var token = urlOrToken;
        var query = token.IndexOf("?token=", StringComparison.Ordinal);
        if (query >= 0)
            token = token[(query + "?token=".Length)..];
        var parts = token.Split('.');
        if (parts.Length != 4)
            return false;
        if (!long.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var expiryUnix))
            return false;
        if (!string.Equals(parts[2], purpose, StringComparison.Ordinal))
            return false;
        string key;
        try
        {
            key = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = ComputeToken(parts[0], parts[1], parts[2]);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected),
                Encoding.ASCII.GetBytes(parts[3])))
            return false;
        if (DateTimeOffset.FromUnixTimeSeconds(expiryUnix) + ExpiryGrace < DateTimeOffset.UtcNow)
            return false;
        // The encoded key segment is base64url("tenant\nobjectKey").
        var newline = key.IndexOf('\n');
        if (newline <= 0)
            return false;
        tenantId = key[..newline];
        objectKey = key[(newline + 1)..];
        return tenantId.Length > 0 && objectKey.Length > 0;
    }

    public string ResolvePath(string objectKey)
    {
        if (string.IsNullOrWhiteSpace(objectKey)
            || objectKey.Contains("..", StringComparison.Ordinal)
            || Path.IsPathRooted(objectKey))
            throw new ArgumentException("Unsafe object key.", nameof(objectKey));
        var full = Path.GetFullPath(Path.Combine(_storage.FileRoot, objectKey));
        var root = Path.GetFullPath(_storage.FileRoot);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Object key escapes the file root.", nameof(objectKey));
        return full;
    }

    private string BuildFileToken(string tenantId, string purpose, string objectKey, DateTimeOffset expiresAtUtc)
    {
        var expiry = expiresAtUtc.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var key = Base64UrlEncode(Encoding.UTF8.GetBytes(tenantId + "\n" + objectKey));
        return expiry + "." + key + "." + purpose + "." + ComputeToken(expiry, key, purpose);
    }

    private string ComputeToken(string expiry, string key, string purpose)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_storage.TokenSecret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(expiry + "\n" + key + "\n" + purpose))).ToLowerInvariant();
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}
