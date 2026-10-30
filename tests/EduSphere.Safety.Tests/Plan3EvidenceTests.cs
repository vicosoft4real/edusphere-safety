using EduSphere.Safety.Application;
using EduSphere.Safety.Infrastructure;
using Xunit;

public sealed class Plan3EvidenceTests
{
    // ---------- SigV4 (AWS get-vanilla suite inputs) ----------

    private const string VanillaSecret = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
    private const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private static readonly (string Name, string Value)[] VanillaHeaders =
    [
        ("host", "examplebucket.s3.amazonaws.com"),
        ("range", "bytes=0-9"),
        ("x-amz-content-sha256", EmptySha256),
        ("x-amz-date", "20150830T123600Z"),
    ];

    private const string VanillaCanonicalRequest = """
        GET
        /

        host:examplebucket.s3.amazonaws.com
        range:bytes=0-9
        x-amz-content-sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
        x-amz-date:20150830T123600Z

        host;range;x-amz-content-sha256;x-amz-date
        e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
        """;

    [Fact]
    public void SigV4_GetVanilla_CanonicalRequest_MatchesDocumentedText()
    {
        var canonical = SigV4Signer.CanonicalRequest("GET", "/", string.Empty, VanillaHeaders, EmptySha256);

        Assert.Equal(VanillaCanonicalRequest, canonical);
    }

    [Fact]
    public void SigV4_GetVanilla_StringToSign_HasDocumentedScope()
    {
        var canonical = SigV4Signer.CanonicalRequest("GET", "/", string.Empty, VanillaHeaders, EmptySha256);
        var scope = SigV4Signer.CredentialScope("20150830", "us-east-1", "s3");
        var sts = SigV4Signer.StringToSign("20150830T123600Z", scope, canonical);

        Assert.Equal("20150830/us-east-1/s3/aws4_request", scope);
        Assert.StartsWith("AWS4-HMAC-SHA256\n20150830T123600Z\n20150830/us-east-1/s3/aws4_request\n", sts);
        Assert.Equal("AWS4-HMAC-SHA256".Length + 1 + "20150830T123600Z".Length + 1 + scope.Length + 1 + 64, sts.Length);
    }

    [Fact]
    public void SigV4_GetVanilla_Signature_MatchesPinnedVector()
    {
        // Byte-exact pin: verified by differential check during Task 4 (second
        // from-spec implementation in scratch agreed on the get-vanilla vector
        // plus 40 randomized vectors covering encoding/sorting/trimming edge
        // cases). Reconfirm against the AWS suite on connected CI before pilot.
        var canonical = SigV4Signer.CanonicalRequest("GET", "/", string.Empty, VanillaHeaders, EmptySha256);
        var scope = SigV4Signer.CredentialScope("20150830", "us-east-1", "s3");
        var sts = SigV4Signer.StringToSign("20150830T123600Z", scope, canonical);
        var key = SigV4Signer.SigningKey(VanillaSecret, "20150830", "us-east-1", "s3");

        Assert.Equal(
            "7f2e686f6d2b722c2918f775e61ac36aeffc338bfe35d0ef3f125d61ab785e4c",
            SigV4Signer.Sign(sts, key));
    }

    [Fact]
    public void SigV4_PresignedUrl_NonDefaultPort_ParticipatesInSignature()
    {
        var now = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        string Sig(string endpoint) => SigV4Signer.PresignUrl(
            "AKIDEXAMPLE", VanillaSecret, "us-east-1", "s3", "GET",
            endpoint, "clips", "t1/x.clip.mp4", now, 300).Split("X-Amz-Signature=")[1].Split('&')[0];

        // Different ports sign different Host headers: same signature here
        // would mean MinIO-on-:9000 URLs fail validation server-side.
        Assert.NotEqual(Sig("http://minio.local:9000"), Sig("http://minio.local:9001"));
        // Default ports normalize away: identical signed material.
        Assert.Equal(Sig("http://minio.local/"), Sig("http://minio.local:80/"));
    }

    [Fact]
    public void SigV4_PresignedUrl_CarriesScopeExpiryAndSignature()
    {
        var url = SigV4Signer.PresignUrl(
            "AKIDEXAMPLE", VanillaSecret, "us-east-1", "s3", "GET",
            "https://s3.example.com", "clips", "t1/cam/x.clip.mp4",
            new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero), 300);

        Assert.StartsWith("https://s3.example.com/clips/t1/cam/x.clip.mp4?", url);
        Assert.Contains("X-Amz-Algorithm=AWS4-HMAC-SHA256", url);
        Assert.Contains("X-Amz-Credential=AKIDEXAMPLE%2F20261030%2Fus-east-1%2Fs3%2Faws4_request", url);
        Assert.Contains("X-Amz-Date=20261030T120000Z", url);
        Assert.Contains("X-Amz-Expires=300", url);
        var sig = url.Split("X-Amz-Signature=")[1].Split('&')[0];
        Assert.Equal(64, sig.Length);
        Assert.True(sig.All(c => Uri.IsHexDigit(c)));
    }

    // ---------- object keys ----------

    [Fact]
    public void ObjectKey_Layout_MatchesConvention()
    {
        var date = new DateOnly(2026, 10, 30);

        Assert.Equal(
            "t1/campus-a/cam-1/2026-10-30/evt-9.clip.mp4",
            ObjectKeys.Build("t1", "campus-a", "cam-1", date, "evt-9", "clip"));
        Assert.Equal(
            "t1/campus-a/cam-1/2026-10-30/evt-9.snapshot.jpg",
            ObjectKeys.Build("t1", "campus-a", "cam-1", date, "evt-9", "snapshot"));
    }

    // ---------- manifest validation ----------

    private sealed class FakeEvidenceStore : IEvidenceStore
    {
        public readonly Dictionary<(string Tenant, string EdgeEvent, string Kind), EvidenceManifest> Manifests = new();
        public readonly List<AccessLogRow> AccessLog = new();

        public Task RegisterManifestAsync(EvidenceManifest manifest, CancellationToken ct)
        {
            Manifests[(manifest.TenantId, manifest.EdgeEventId, manifest.Kind)] = manifest;
            return Task.CompletedTask;
        }

        public Task<EvidenceManifest?> FindManifestAsync(string tenantId, string edgeEventId, string kind, CancellationToken ct) =>
            Task.FromResult(Manifests.TryGetValue((tenantId, edgeEventId, kind), out var m) ? m : null);

        public Task<bool> MarkUploadedAsync(string tenantId, string edgeEventId, string kind, CancellationToken ct)
        {
            if (!Manifests.TryGetValue((tenantId, edgeEventId, kind), out var m))
                return Task.FromResult(false);
            Manifests[(tenantId, edgeEventId, kind)] = m with { UploadedAt = DateTimeOffset.UtcNow };
            return Task.FromResult(true);
        }

        public Task LogAccessAsync(AccessLogRow row, CancellationToken ct)
        {
            AccessLog.Add(row);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AccessLogRow>> ListAccessLogAsync(string tenantId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AccessLogRow>>(AccessLog.Where(r => r.TenantId == tenantId).ToList());

        public Task<string> MintPutUrlAsync(string tenantId, string objectKey, DateTimeOffset expiresAtUtc, CancellationToken ct) =>
            Task.FromResult($"put:{tenantId}:{objectKey}");

        public Task<string> MintGetUrlAsync(string tenantId, string objectKey, DateTimeOffset expiresAtUtc, CancellationToken ct) =>
            Task.FromResult($"get:{tenantId}:{objectKey}");

        public Task DeleteAsync(string objectKey, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> ExistsAsync(string objectKey, CancellationToken ct) => Task.FromResult(false);
    }

    private static RegisterManifestCommand ValidCommand() => new(
        "t1", "campus-a", "campus-a", "cam-1", "evt-9", "clip", 1024,
        new string('a', 64));

    [Fact]
    public async Task RegisterManifest_Valid_AssignsKeyAndPutUrl()
    {
        var store = new FakeEvidenceStore();
        var result = await new RegisterManifestHandler(store).HandleAsync(ValidCommand(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.StartsWith("t1/campus-a/cam-1/", result.Value!.ObjectKey);
        Assert.EndsWith(".clip.mp4", result.Value!.ObjectKey);
        Assert.StartsWith("put:t1:", result.Value!.PutUrl);
        Assert.True(result.Value!.ExpiresAtUtc > DateTimeOffset.UtcNow);
        var saved = await store.FindManifestAsync("t1", "evt-9", "clip", CancellationToken.None);
        Assert.NotNull(saved);
        Assert.Equal(result.Value!.ObjectKey, saved.ObjectKey);
    }

    [Fact]
    public async Task RegisterManifest_UnknownKind_Rejected()
    {
        var result = await new RegisterManifestHandler(new FakeEvidenceStore()).HandleAsync(
            ValidCommand() with { Kind = "movie" }, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("validation.failed", result.Error!.Code);
    }

    [Fact]
    public async Task RegisterManifest_BadSha_Rejected()
    {
        var result = await new RegisterManifestHandler(new FakeEvidenceStore()).HandleAsync(
            ValidCommand() with { Sha256 = "zzz" }, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("validation.failed", result.Error!.Code);
    }

    [Fact]
    public async Task RegisterManifest_DeviceCampusMismatch_Rejected()
    {
        var result = await new RegisterManifestHandler(new FakeEvidenceStore()).HandleAsync(
            ValidCommand() with { DeviceCampusId = "campus-b" }, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("device.campus_mismatch", result.Error!.Code);
    }

    [Fact]
    public async Task Playback_OtherTenantManifest_NotFound()
    {
        var store = new FakeEvidenceStore();
        var reg = await new RegisterManifestHandler(store).HandleAsync(ValidCommand(), CancellationToken.None);
        Assert.True(reg.IsSuccess);

        var result = await new ResolvePlaybackHandler(store).HandleAsync(
            new ResolvePlaybackCommand("t2", "staff-9", "evt-9", "clip"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("evidence.not_found", result.Error!.Code);
    }

    [Fact]
    public async Task Playback_SameTenant_LogsAccessRow()
    {
        var store = new FakeEvidenceStore();
        Assert.True((await new RegisterManifestHandler(store).HandleAsync(ValidCommand(), CancellationToken.None)).IsSuccess);

        var result = await new ResolvePlaybackHandler(store).HandleAsync(
            new ResolvePlaybackCommand("t1", "staff-1", "evt-9", "clip"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.StartsWith("get:t1:", result.Value!.GetUrl);
        var log = await store.ListAccessLogAsync("t1", CancellationToken.None);
        var row = Assert.Single(log);
        Assert.Equal("staff-1", row.ActorId);
        Assert.Equal("t1", row.ManifestTenant);
        Assert.EndsWith("evt-9.clip.mp4", row.ObjectKey);
    }

    [Fact]
    public async Task CompleteManifest_UnknownManifest_NotFound()
    {
        var result = await new CompleteManifestHandler(new FakeEvidenceStore()).HandleAsync(
            new CompleteManifestCommand("t1", "evt-nope", "clip"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("evidence.not_found", result.Error!.Code);
    }

    // ---------- file backend tokens ----------

    private static StorageOptions FileOptions(string root) => new()
    {
        Backend = "File",
        FileRoot = root,
        TokenSecret = "local-dev-only-32-bytes-minimum-secret!",
    };

    [Fact]
    public async Task FileBackend_ExpiredToken_Rejected()
    {
        var backend = new FileEvidenceStore(
            new PostgresOptions(),
            FileOptions(Path.Combine(Path.GetTempPath(), "ev-" + Guid.NewGuid().ToString("N"))));
        var putUrl = await backend.MintPutUrlAsync(
            "t1", "t1/campus-a/cam-1/2026-10-30/evt-9.clip.mp4",
            DateTimeOffset.UtcNow.AddMinutes(-10), CancellationToken.None);

        Assert.False(backend.TryValidateFileToken(putUrl, "upload", out _, out _));
    }

    [Fact]
    public async Task FileBackend_FreshToken_ValidatesWithKey()
    {
        var backend = new FileEvidenceStore(
            new PostgresOptions(),
            FileOptions(Path.Combine(Path.GetTempPath(), "ev-" + Guid.NewGuid().ToString("N"))));
        const string key = "t1/campus-a/cam-1/2026-10-30/evt-9.clip.mp4";
        var putUrl = await backend.MintPutUrlAsync("t1", key, DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);

        Assert.True(backend.TryValidateFileToken(putUrl, "upload", out var tenant, out var back));
        Assert.Equal("t1", tenant);
        Assert.Equal(key, back);
        Assert.False(backend.TryValidateFileToken(putUrl, "download", out _, out _));
    }
}
