using System.Security.Cryptography;
using System.Text;

namespace EduSphere.Safety.Infrastructure;

/// <summary>
/// Minimal AWS Signature Version 4 implementation for S3-compatible storage
/// (MinIO), written by hand so the offline build needs no AWS SDK:
/// canonical request, string-to-sign, signing-key chain, header authorization,
/// and query-string presigning. Path-style addressing (MinIO default).
/// Follows the SigV4 spec line by line: header names lowercased and trimmed,
/// sorted; RFC 3986 percent-encoding with uppercase hex; dates as
/// yyyyMMdd / yyyyMMddTHHmmssZ (UTC).
/// </summary>
public static class SigV4Signer
{
    public const string Algorithm = "AWS4-HMAC-SHA256";
    public const string Terminator = "aws4_request";

    public static string CredentialScope(string dateStamp, string region, string service) =>
        $"{dateStamp}/{region}/{service}/{Terminator}";

    public static string CanonicalRequest(
        string method,
        string path,
        string canonicalQueryString,
        IReadOnlyList<(string Name, string Value)> headers,
        string payloadHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadHash);

        var ordered = headers
            .Select(h => (Name: h.Name.Trim().ToLowerInvariant(), Value: h.Value.Trim()))
            .OrderBy(h => h.Name, StringComparer.Ordinal)
            .ToList();
        var canonicalHeaders = new StringBuilder();
        foreach (var h in ordered)
            canonicalHeaders.Append(h.Name).Append(':').Append(h.Value).Append('\n');
        var signedHeaders = string.Join(";", ordered.Select(h => h.Name));

        return string.Join("\n",
            method.Trim().ToUpperInvariant(),
            EncodePath(path),
            canonicalQueryString,
            canonicalHeaders.ToString(),
            signedHeaders,
            payloadHash);
    }

    public static string StringToSign(string amzDate, string credentialScope, string canonicalRequest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(amzDate);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialScope);
        ArgumentNullException.ThrowIfNull(canonicalRequest);
        return string.Join("\n", Algorithm, amzDate, credentialScope, Sha256Hex(canonicalRequest));
    }

    public static byte[] SigningKey(string secretKey, string dateStamp, string region, string service)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);
        var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), dateStamp);
        key = Hmac(key, region);
        key = Hmac(key, service);
        return Hmac(key, Terminator);
    }

    public static string Sign(string stringToSign, byte[] signingKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stringToSign);
        ArgumentNullException.ThrowIfNull(signingKey);
        return Convert.ToHexString(Hmac(signingKey, stringToSign)).ToLowerInvariant();
    }

    public static string Authorization(
        string accessKey, string credentialScope, string signedHeaders, string signature) =>
        $"{Algorithm} Credential={accessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";

    /// <summary>
    /// Builds a presigned URL: SigV4 parameters in the query string, signature
    /// last. Unsigned payload ("UNSIGNED-PAYLOAD") since content is not present
    /// at signing time. Returns the absolute URL.
    /// </summary>
    public static string PresignUrl(
        string accessKey,
        string secretKey,
        string region,
        string service,
        string method,
        string endpoint,
        string bucket,
        string objectKey,
        DateTimeOffset nowUtc,
        int expiresSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        if (expiresSeconds <= 0 || expiresSeconds > 604800)
            throw new ArgumentOutOfRangeException(nameof(expiresSeconds), "Presign expiry must be 1s..7d.");

        var dateStamp = nowUtc.ToUniversalTime().ToString("yyyyMMdd");
        var amzDate = nowUtc.ToUniversalTime().ToString("yyyyMMddTHHmmssZ");
        var scope = CredentialScope(dateStamp, region, service);
        var path = "/" + bucket.Trim('/') + "/" + objectKey.TrimStart('/');

        var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Amz-Algorithm"] = Algorithm,
            ["X-Amz-Credential"] = accessKey + "/" + scope,
            ["X-Amz-Date"] = amzDate,
            ["X-Amz-Expires"] = expiresSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["X-Amz-SignedHeaders"] = "host",
        };
        var canonicalQuery = string.Join("&",
            query.Select(kv => Encode(kv.Key) + "=" + Encode(kv.Value)));

        // Authority (host + non-default port) is what HTTP clients actually send
        // as the Host header; signing bare Host would break MinIO on :9000.
        var host = new Uri(endpoint.TrimEnd('/'), UriKind.Absolute).Authority;
        var canonical = CanonicalRequest(
            method, path, canonicalQuery,
            [("host", host)],
            "UNSIGNED-PAYLOAD");
        var sts = StringToSign(amzDate, scope, canonical);
        var signature = Sign(sts, SigningKey(secretKey, dateStamp, region, service));

        return endpoint.TrimEnd('/') + EncodePath(path) + "?" + canonicalQuery
            + "&X-Amz-Signature=" + signature;
    }

    public static string Sha256Hex(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static byte[] Hmac(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    /// <summary>RFC 3986 encoding: unreserved marks stay, all else %XX uppercase.</summary>
    public static string Encode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = Encoding.UTF8.GetBytes(value);
        var output = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            var c = (char)b;
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                || c == '-' || c == '_' || c == '.' || c == '~')
                output.Append(c);
            else
                output.Append('%').Append(b.ToString("X2"));
        }

        return output.ToString();
    }

    private static string EncodePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return string.Join("/", path.Split('/').Select(Encode));
    }
}
