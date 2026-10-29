using System.Security.Cryptography;
using System.Text;

namespace EduSphere.Safety.Api;

/// <summary>
/// Device authentication for POST /v1/ingest/events.
/// Header format: <c>X-Device-Key: &lt;deviceId&gt;.&lt;secret&gt;</c>.
/// The secret is SHA256-hashed (lowercase hex) and compared to the stored
/// key_hash in constant time. Unknown or revoked devices get 401.
/// Tenant always comes from the device row, never from the request body.
/// </summary>
public static class DeviceAuth
{
    public const string HeaderName = "X-Device-Key";

    public static bool TryParseHeader(string? header, out string deviceId, out string secret)
    {
        deviceId = string.Empty;
        secret = string.Empty;
        if (string.IsNullOrWhiteSpace(header))
            return false;
        var dot = header.IndexOf('.');
        if (dot <= 0 || dot == header.Length - 1)
            return false;
        deviceId = header[..dot];
        secret = header[(dot + 1)..];
        return deviceId.Length > 0 && secret.Length > 0;
    }

    public static string HashSecret(string secret)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool SecretMatches(string secret, string? storedKeyHash)
    {
        var computed = Encoding.UTF8.GetBytes(HashSecret(secret));
        var stored = Encoding.UTF8.GetBytes((storedKeyHash ?? string.Empty).ToLowerInvariant());
        if (computed.Length != stored.Length)
            return false;
        return CryptographicOperations.FixedTimeEquals(computed, stored);
    }
}
