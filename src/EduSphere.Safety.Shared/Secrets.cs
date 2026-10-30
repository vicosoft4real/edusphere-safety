using System.Security.Cryptography;

namespace EduSphere.Safety.Shared;

/// <summary>
/// Secret handling primitives shared by device enrolment (Application) and
/// device authentication (Api). Secrets are crypto-random; only SHA256 hex
/// digests are stored or compared, always in constant time.
/// </summary>
public static class Secrets
{
    public static string Sha256Hex(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    public static string NewUrlSafeSecret(int bytes)
    {
        if (bytes < 16)
            throw new ArgumentOutOfRangeException(nameof(bytes), "Secrets must carry at least 128 bits of entropy.");
        return Base64Url(RandomNumberGenerator.GetBytes(bytes));
    }

    /// <summary>
    /// Non-secret public identifiers (device ids). Uniqueness comes from the
    /// byte length, not from a floor: these are looked up, never guessed as
    /// credentials (device auth needs the separate secret).
    /// </summary>
    public static string NewUrlSafeId(int bytes)
    {
        if (bytes < 1)
            throw new ArgumentOutOfRangeException(nameof(bytes), "Id must carry at least one byte.");
        return Base64Url(RandomNumberGenerator.GetBytes(bytes));
    }

    public static bool FixedTimeEqualsHex(string a, string b)
    {
        var ab = System.Text.Encoding.UTF8.GetBytes((a ?? string.Empty).ToLowerInvariant());
        var bb = System.Text.Encoding.UTF8.GetBytes((b ?? string.Empty).ToLowerInvariant());
        if (ab.Length != bb.Length)
            return false;
        return CryptographicOperations.FixedTimeEquals(ab, bb);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
