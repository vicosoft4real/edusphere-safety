using EduSphere.Safety.Application;

namespace EduSphere.Safety.Infrastructure;

/// <summary>
/// S3-compatible evidence backend (MinIO): manifests + access log in
/// PostgreSQL (shared repository), bytes in the bucket. Upload/download URLs
/// are SigV4 presigned (hand-rolled, no SDK): PUT valid 15 minutes, GET valid
/// 5 minutes. Object keys are tenant-scoped but never access control — every
/// access goes through a signed URL.
/// </summary>
public sealed class S3EvidenceStore : IEvidenceStore
{
    private readonly EvidenceManifestRepository _manifests;
    private readonly StorageOptions _storage;
    private readonly HttpClient _http;

    public S3EvidenceStore(PostgresOptions postgres, StorageOptions storage)
        : this(postgres, storage, new HttpClient())
    {
    }

    internal S3EvidenceStore(PostgresOptions postgres, StorageOptions storage, HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(http);
        _manifests = new EvidenceManifestRepository(postgres);
        _storage = storage;
        _http = http;
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
        Task.FromResult(Presign("PUT", objectKey, expiresAtUtc, EvidencePolicy.PutUrlTtlMinutes * 60));

    public Task<string> MintGetUrlAsync(string tenantId, string objectKey, DateTimeOffset expiresAtUtc, CancellationToken ct) =>
        Task.FromResult(Presign("GET", objectKey, expiresAtUtc, EvidencePolicy.GetUrlTtlMinutes * 60));

    public async Task DeleteAsync(string objectKey, CancellationToken ct)
    {
        var request = SignedRequest("DELETE", objectKey, DateTimeOffset.UtcNow);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<bool> ExistsAsync(string objectKey, CancellationToken ct)
    {
        var request = SignedRequest("HEAD", objectKey, DateTimeOffset.UtcNow);
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    private string Presign(string method, string objectKey, DateTimeOffset expiresAtUtc, int maxSeconds)
    {
        var ttl = expiresAtUtc - DateTimeOffset.UtcNow;
        var seconds = (int)Math.Max(1, Math.Min(maxSeconds, ttl.TotalSeconds));
        return SigV4Signer.PresignUrl(
            _storage.AccessKey, _storage.SecretKey, _storage.Region, "s3",
            method, _storage.Endpoint, _storage.Bucket, objectKey,
            DateTimeOffset.UtcNow, seconds);
    }

    private HttpRequestMessage SignedRequest(string method, string objectKey, DateTimeOffset nowUtc)
    {
        var dateStamp = nowUtc.ToUniversalTime().ToString("yyyyMMdd");
        var amzDate = nowUtc.ToUniversalTime().ToString("yyyyMMddTHHmmssZ");
        var scope = SigV4Signer.CredentialScope(dateStamp, _storage.Region, "s3");
        var path = "/" + _storage.Bucket.Trim('/') + "/" + objectKey.TrimStart('/');
        var uri = new Uri(_storage.Endpoint.TrimEnd('/') + path, UriKind.Absolute);
        var canonical = SigV4Signer.CanonicalRequest(
            method, path, string.Empty,
            [("host", uri.Authority), ("x-amz-content-sha256", EmptyPayloadHash), ("x-amz-date", amzDate)],
            EmptyPayloadHash);
        var signature = SigV4Signer.Sign(
            SigV4Signer.StringToSign(amzDate, scope, canonical),
            SigV4Signer.SigningKey(_storage.SecretKey, dateStamp, _storage.Region, "s3"));

        var request = new HttpRequestMessage(new HttpMethod(method), uri);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", EmptyPayloadHash);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            SigV4Signer.Authorization(
                _storage.AccessKey, scope, "host;x-amz-content-sha256;x-amz-date", signature));
        return request;
    }

    private const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
}
