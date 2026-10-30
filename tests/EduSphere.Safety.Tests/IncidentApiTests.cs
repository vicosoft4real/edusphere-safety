using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EduSphere.Safety.Api;
using EduSphere.Safety.Application;
using EduSphere.Safety.Core;
using EduSphere.Safety.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class SafetyApiFactory : WebApplicationFactory<Program>
{
    public const string SigningKey = "local-dev-only-32-bytes-minimum-key!!";
    public const string Issuer = "edusphere-dev";
    public const string Audience = "edusphere-safety";

    public FakeApiStores Stores { get; } = new();

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("Auth:DevStubEnabled", "true");
        builder.UseSetting("Auth:IssuerSigningKey", SigningKey);
        // High ceiling so suites that enrol repeatedly never trip the throttle;
        // RateLimitTests sets its own low ceiling via ThrottledFactory.
        builder.UseSetting("RateLimit:CodeGatedPermitLimit", "1000");
        builder.UseSetting("Auth:Issuer", Issuer);
        builder.UseSetting("Auth:Audience", Audience);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IIncidentStore>(Stores.Incidents);
            services.AddSingleton<IDeviceStore>(Stores.Devices);
            services.AddSingleton<IIncidentBroadcaster>(Stores.Broadcast);
            services.AddSingleton<ICameraStore>(Stores.Cameras);
            services.AddSingleton<IConfigStore>(Stores.Config);
            services.AddSingleton<IEvidenceStore>(Stores.Evidence);
            services.AddSingleton<INotificationStore>(Stores.Notifications);
        });
    }

    public static string UserToken(string tenantId, string userId) =>
        DevTokenIssuer.Mint(SigningKey, Issuer, Audience, tenantId, userId);

    public static string DeviceHeader(string deviceId, string secret) => $"{deviceId}.{secret}";
}

public sealed class FakeApiStores
{
    public FakeApiIncidentStore Incidents { get; } = new();
    public FakeApiDeviceStore Devices { get; } = new();
    public RecordingBroadcaster Broadcast { get; } = new();
    public FakeApiCameraStore Cameras { get; } = new();
    public FakeApiConfigStore Config { get; } = new();
    public FakeApiEvidenceStore Evidence { get; } = new();
    public FakeApiNotificationStore Notifications { get; }

    public FakeApiStores()
    {
        Notifications = new FakeApiNotificationStore(Incidents);
    }

    public void Reset()
    {
        Incidents.Reset();
        Devices.Reset();
        Broadcast.Reset();
        Cameras.Reset();
        Config.Reset();
        Evidence.Reset();
        Notifications.Reset();
    }
}

public sealed class FakeApiCameraStore : ICameraStore
{
    public Dictionary<string, CameraRecord> Cameras { get; } = new();

    private static string Key(string tenantId, string cameraId) => $"{tenantId}\0{cameraId}";

    public Task InsertAsync(CameraRecord camera, CancellationToken ct)
    {
        Cameras[Key(camera.TenantId, camera.CameraId)] = camera;
        return Task.CompletedTask;
    }

    public Task<bool> ReplaceAsync(CameraRecord camera, CancellationToken ct)
    {
        var key = Key(camera.TenantId, camera.CameraId);
        if (!Cameras.ContainsKey(key))
            return Task.FromResult(false);
        Cameras[key] = camera;
        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string tenantId, string cameraId, CancellationToken ct) =>
        Task.FromResult(Cameras.Remove(Key(tenantId, cameraId)));

    public Task<CameraRecord?> FindAsync(string tenantId, string cameraId, CancellationToken ct) =>
        Task.FromResult(Cameras.TryGetValue(Key(tenantId, cameraId), out var c) ? c : null);

    public Task<IReadOnlyList<CameraRecord>> ListAsync(string tenantId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<CameraRecord>>(
            Cameras.Values.Where(c => c.TenantId == tenantId).OrderBy(c => c.CameraId).ToList());

    public Task<bool> ReportTestAsync(
        string tenantId, string cameraId, bool ok, string detail, DateTimeOffset at, CancellationToken ct)
    {
        var key = Key(tenantId, cameraId);
        if (!Cameras.TryGetValue(key, out var c))
            return Task.FromResult(false);
        Cameras[key] = c with { LastTestOk = ok, LastTestDetail = detail, LastTestAt = at };
        return Task.FromResult(true);
    }

    public void Reset() => Cameras.Clear();
}

public sealed class FakeApiIncidentStore : IIncidentStore
{
    private sealed record Snapshot(
        Guid Id, string TenantId, string CampusId, string EventType, string CameraId,
        string? EdgeEventId, IncidentState State, int Version);

    private readonly Dictionary<(string TenantId, Guid IncidentId), Snapshot> _snapshots = new();
    public List<OutboxEntry> Outbox { get; } = [];
    public List<DeadLetterRow> DeadLetters { get; } = [];

    private static Snapshot Take(Incident i) => new(
        i.Id, i.TenantId, i.CampusId, i.EventType, i.CameraId, i.EdgeEventId, i.State, i.Version);

    private static Incident Materialize(Snapshot s) => Incident.Rehydrate(
        s.Id, s.TenantId, s.CampusId, s.EventType, s.CameraId, s.EdgeEventId, s.State, s.Version);

    public Task<Incident?> FindByEdgeEventAsync(string tenantId, string edgeEventId, CancellationToken ct)
    {
        var s = _snapshots.Values.FirstOrDefault(x => x.TenantId == tenantId && x.EdgeEventId == edgeEventId);
        return Task.FromResult(s is null ? null : Materialize(s));
    }

    public Task<Incident?> GetAsync(string tenantId, Guid incidentId, CancellationToken ct) =>
        Task.FromResult(_snapshots.TryGetValue((tenantId, incidentId), out var s) ? Materialize(s) : null);

    public Task<IReadOnlyList<Incident>> ListAsync(string tenantId, int limit, int offset, CancellationToken ct)
    {
        IReadOnlyList<Incident> page = _snapshots.Values
            .Where(s => s.TenantId == tenantId).OrderBy(s => s.Id)
            .Skip(Math.Max(offset, 0)).Take(Math.Clamp(limit, 1, 200))
            .Select(Materialize).ToList();
        return Task.FromResult(page);
    }

    public Task<IReadOnlyList<DeadLetterRow>> ListDeadLetterAsync(string tenantId, int limit, CancellationToken ct)
    {
        IReadOnlyList<DeadLetterRow> rows = DeadLetters
            .Where(d => d.TenantId == tenantId && d.Attempts >= 10)
            .Take(Math.Max(limit, 0)).ToList();
        return Task.FromResult(rows);
    }

    public Task<Guid> AddAsync(Incident incident, OutboxEntry outboxEntry, CancellationToken ct)
    {
        // Mirrors the real store's conflict path: a pre-seeded
        // (tenant, edge) key wins — return the existing id and append NO
        // second outbox row.
        if (incident.EdgeEventId is not null)
        {
            var conflict = _snapshots.Values
                .FirstOrDefault(s => s.TenantId == incident.TenantId && s.EdgeEventId == incident.EdgeEventId);
            if (conflict is not null)
                return Task.FromResult(conflict.Id);
        }

        _snapshots[(incident.TenantId, incident.Id)] = Take(incident);
        Outbox.Add(outboxEntry);
        return Task.FromResult(incident.Id);
    }

    public void Reset()
    {
        _snapshots.Clear();
        Outbox.Clear();
        DeadLetters.Clear();
    }

    public Task<bool> SaveAsync(
        Incident incident, int expectedVersion, ActivityEntry activityEntry, OutboxEntry outboxEntry, CancellationToken ct)
    {
        var key = (incident.TenantId, incident.Id);
        if (!_snapshots.TryGetValue(key, out var stored) || stored.Version != expectedVersion)
            return Task.FromResult(false);
        _snapshots[key] = Take(incident);
        Outbox.Add(outboxEntry);
        return Task.FromResult(true);
    }
}

public sealed class FakeApiDeviceStore : IDeviceStore
{
    public Dictionary<string, DeviceRecord> Devices { get; } = new();
    public HashSet<(string TenantId, string UserId)> Members { get; } = new();
    public Dictionary<string, (string TenantId, string? CampusId, DateTimeOffset Expires, bool Used)> Codes { get; } = new();

    public Task<DeviceRecord?> FindByIdAsync(string deviceId, CancellationToken ct) =>
        Task.FromResult(Devices.TryGetValue(deviceId, out var d) ? d : null);

    public Task<bool> IsTenantMemberAsync(string tenantId, string userId, CancellationToken ct) =>
        Task.FromResult(Members.Contains((tenantId, userId)));

    public Task CreateEnrolCodeAsync(
        string tenantId, string? campusId, string codeHash,
        DateTimeOffset expiresAt, string createdBy, CancellationToken ct)
    {
        Codes[codeHash] = (tenantId, campusId, expiresAt, false);
        return Task.CompletedTask;
    }

    public Task<EnrolGrant?> ConsumeEnrolCodeAsync(string codeHash, DateTimeOffset nowUtc, CancellationToken ct)
    {
        if (!Codes.TryGetValue(codeHash, out var c) || c.Used || c.Expires <= nowUtc)
            return Task.FromResult<EnrolGrant?>(null);
        Codes[codeHash] = (c.TenantId, c.CampusId, c.Expires, true);
        return Task.FromResult<EnrolGrant?>(new EnrolGrant(c.TenantId, c.CampusId));
    }

    public Task RegisterDeviceAsync(DeviceRecord device, CancellationToken ct)
    {
        Devices[device.DeviceId] = device;
        return Task.CompletedTask;
    }

    public Task<bool> RotateSecretAsync(string tenantId, string deviceId, string newKeyHash, CancellationToken ct)
    {
        if (!Devices.TryGetValue(deviceId, out var d) || d.TenantId != tenantId)
            return Task.FromResult(false);
        Devices[deviceId] = d with { KeyHash = newKeyHash };
        return Task.FromResult(true);
    }

    public Task<bool> SetRevokedAsync(string tenantId, string deviceId, bool revoked, CancellationToken ct)
    {
        if (!Devices.TryGetValue(deviceId, out var d) || d.TenantId != tenantId)
            return Task.FromResult(false);
        Devices[deviceId] = d with { Revoked = revoked };
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<DeviceRecord>> ListAsync(string tenantId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DeviceRecord>>(
            Devices.Values.Where(d => d.TenantId == tenantId).ToList());

    public Task RecordHeartbeatAsync(
        string tenantId, string deviceId,
        string softwareVersion, string modelVersion, int configVersion,
        CancellationToken ct)
    {
        if (Devices.TryGetValue(deviceId, out var d) && d.TenantId == tenantId)
        {
            Devices[deviceId] = d with
            {
                SoftwareVersion = softwareVersion,
                ModelVersion = modelVersion,
                ConfigVersion = configVersion,
                LastHeartbeatAt = DateTimeOffset.UtcNow,
            };
        }

        return Task.CompletedTask;
    }

    public void Reset()
    {
        Devices.Clear();
        Members.Clear();
        Codes.Clear();
    }
}

public sealed class RecordingBroadcaster : IIncidentBroadcaster
{
    public List<(string Method, string TenantId, string CampusId)> Calls { get; } = [];

    public Task IncidentCreatedAsync(string tenantId, string campusId, object payload, CancellationToken ct)
    {
        Calls.Add(("incident.created", tenantId, campusId));
        return Task.CompletedTask;
    }

    public Task IncidentUpdatedAsync(string tenantId, string campusId, object payload, CancellationToken ct)
    {
        Calls.Add(("incident.updated", tenantId, campusId));
        return Task.CompletedTask;
    }

    public void Reset() => Calls.Clear();
}

public sealed class FakeApiConfigStore : IConfigStore
{
    private readonly Dictionary<string, List<BundleRecord>> _bundles = new();
    private readonly Dictionary<string, List<PolicyRecord>> _policies = new();

    public Task<int> PublishBundleAsync(string tenantId, string document, string createdBy, CancellationToken ct)
    {
        if (!_bundles.TryGetValue(tenantId, out var list))
        {
            list = [];
            _bundles[tenantId] = list;
        }

        list.Add(new BundleRecord(tenantId, list.Count + 1, document, createdBy, DateTimeOffset.UtcNow));
        return Task.FromResult(list.Count);
    }

    public Task<BundleRecord?> GetLatestBundleAsync(string tenantId, CancellationToken ct) =>
        Task.FromResult(_bundles.TryGetValue(tenantId, out var list) && list.Count > 0 ? list[^1] : null);

    public Task<PolicyRecord?> GetPolicyAsync(string tenantId, string id, CancellationToken ct) =>
        Task.FromResult(_policies.TryGetValue(tenantId, out var list) ? list.FirstOrDefault(p => p.Id == id) : null);

    public Task SetPolicyAsync(PolicyRecord policy, CancellationToken ct)
    {
        if (!_policies.TryGetValue(policy.TenantId, out var list))
        {
            list = [];
            _policies[policy.TenantId] = list;
        }

        list.RemoveAll(p => p.Id == policy.Id);
        list.Add(policy);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PolicyRecord>> ListPoliciesAsync(string tenantId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PolicyRecord>>(
            _policies.TryGetValue(tenantId, out var list) ? list.ToList() : []);

    public async Task<ActiveConfig?> GetActiveConfigAsync(string tenantId, CancellationToken ct)
    {
        var bundle = await GetLatestBundleAsync(tenantId, ct);
        if (bundle is null)
            return null;
        return new ActiveConfig(tenantId, bundle.Version, bundle.Document, await ListPoliciesAsync(tenantId, ct));
    }

    public void Reset()
    {
        _bundles.Clear();
        _policies.Clear();
    }
}

public sealed class FakeApiEvidenceStore : IEvidenceStore
{
    public Dictionary<(string Tenant, string EdgeEvent, string Kind), EvidenceManifest> Manifests { get; } = new();
    public List<AccessLogRow> AccessLog { get; } = new();
    public List<string> DeletedKeys { get; } = new();

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
        Task.FromResult($"https://evidence.local/put/{objectKey}?sig=test");

    public Task<string> MintGetUrlAsync(string tenantId, string objectKey, DateTimeOffset expiresAtUtc, CancellationToken ct) =>
        Task.FromResult($"https://evidence.local/get/{objectKey}?sig=test");

    public Task DeleteAsync(string objectKey, CancellationToken ct)
    {
        DeletedKeys.Add(objectKey);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string objectKey, CancellationToken ct) => Task.FromResult(false);

    public Task<IReadOnlyList<EvidenceManifest>> ListExpiredManifestsAsync(
        DateTimeOffset clipsOlderThanUtc, DateTimeOffset snapshotsOlderThanUtc, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<EvidenceManifest>>(Manifests.Values
            .Where(m => m.UploadedAt.HasValue
                && (m.Kind == "clip" ? m.UploadedAt <= clipsOlderThanUtc : m.UploadedAt <= snapshotsOlderThanUtc))
            .OrderBy(m => m.UploadedAt).ToList());

    public Task<IReadOnlyList<EvidenceManifest>> ListAbandonedManifestsAsync(
        DateTimeOffset uploadExpiredBeforeUtc, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<EvidenceManifest>>(Manifests.Values
            .Where(m => !m.UploadedAt.HasValue && m.ExpiresAt.HasValue && m.ExpiresAt <= uploadExpiredBeforeUtc)
            .OrderBy(m => m.ExpiresAt).ToList());

    public Task DeleteManifestAsync(string tenantId, string edgeEventId, string kind, CancellationToken ct)
    {
        Manifests.Remove((tenantId, edgeEventId, kind));
        return Task.CompletedTask;
    }

    public Task<int> PurgeAccessLogAsync(DateTimeOffset accessedBeforeUtc, CancellationToken ct)
    {
        var purged = AccessLog.RemoveAll(r => r.AccessedAt < accessedBeforeUtc);
        return Task.FromResult(purged);
    }

    public void Reset()
    {
        Manifests.Clear();
        AccessLog.Clear();
        DeletedKeys.Clear();
    }
}

public sealed class FakeApiNotificationStore(FakeApiIncidentStore incidents) : INotificationStore
{
    public Dictionary<(string Tenant, Guid Incident, int Level), (DateTimeOffset Due, DateTimeOffset? LockedUntil, int Attempts)> Schedules { get; } = new();
    public List<DeliveryAttempt> Attempts { get; } = new();

    public Task ScheduleFirstAsync(string tenantId, Guid incidentId, int level, DateTimeOffset dueAtUtc, CancellationToken ct)
    {
        Schedules.TryAdd((tenantId, incidentId, level), (dueAtUtc, null, 0));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<EscalationClaim>> ClaimDueAsync(int batchSize, DateTimeOffset leaseUntilUtc, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var due = Schedules
            .Where(kv => kv.Value.Due <= now && (kv.Value.LockedUntil is null || kv.Value.LockedUntil < now))
            .OrderBy(kv => kv.Value.Due)
            .Take(Math.Max(batchSize, 1))
            .ToList();
        var claims = new List<EscalationClaim>();
        foreach (var kv in due)
        {
            Schedules[kv.Key] = (kv.Value.Due, leaseUntilUtc, kv.Value.Attempts + 1);
            claims.Add(new EscalationClaim(kv.Key.Tenant, kv.Key.Incident, kv.Key.Level, kv.Value.Due, kv.Value.Attempts + 1));
        }

        return Task.FromResult<IReadOnlyList<EscalationClaim>>(claims);
    }

    public async Task<FireDecision> PrepareFireAsync(string tenantId, Guid incidentId, int level, CancellationToken ct)
    {
        var incident = await incidents.GetAsync(tenantId, incidentId, ct);
        if (incident is null || incident.State != IncidentState.New)
            Schedules.Remove((tenantId, incidentId, level));
        return incident is not null && incident.State == IncidentState.New
            ? FireDecision.Proceed
            : FireDecision.Cancelled;
    }

    public Task SettleFireAsync(
        string tenantId, Guid incidentId, int level,
        IReadOnlyList<DeliveryAttempt> attempts, int? nextLevel, DateTimeOffset? nextDueAtUtc,
        CancellationToken ct)
    {
        Attempts.AddRange(attempts);
        Schedules.Remove((tenantId, incidentId, level));
        if (nextLevel.HasValue && nextDueAtUtc.HasValue)
            Schedules.TryAdd((tenantId, incidentId, nextLevel.Value), (nextDueAtUtc.Value, null, 0));
        return Task.CompletedTask;
    }

    public Task DeferFireAsync(
        string tenantId, Guid incidentId, int level, DateTimeOffset lockedUntilUtc,
        IReadOnlyList<DeliveryAttempt> attempts, CancellationToken ct)
    {
        Attempts.AddRange(attempts);
        if (Schedules.TryGetValue((tenantId, incidentId, level), out var s))
            Schedules[(tenantId, incidentId, level)] = (s.Due, lockedUntilUtc, s.Attempts);
        return Task.CompletedTask;
    }

    public Task CancelScheduleAsync(string tenantId, Guid incidentId, int level, CancellationToken ct)
    {
        Schedules.Remove((tenantId, incidentId, level));
        return Task.CompletedTask;
    }

    public Task RecordAttemptsAsync(IReadOnlyList<DeliveryAttempt> attempts, CancellationToken ct)
    {
        Attempts.AddRange(attempts);
        return Task.CompletedTask;
    }

    public void Reset()
    {
        Schedules.Clear();
        Attempts.Clear();
    }
}

public sealed class IncidentApiTests : IClassFixture<SafetyApiFactory>
{
    private readonly SafetyApiFactory _factory;

    public IncidentApiTests(SafetyApiFactory factory)
    {
        // xUnit constructs a fresh test-class instance per test but the
        // IClassFixture factory is shared: reset ALL mutable fake state here
        // so every test starts from identical deterministic seeds.
        _factory = factory;
        factory.Stores.Reset();
        factory.Stores.Devices.Devices["edge-1"] = new DeviceRecord("t1", "edge-1", DeviceAuth.HashSecret("s3cret"), false);
        factory.Stores.Devices.Devices["edge-revoked"] = new DeviceRecord("t1", "edge-revoked", DeviceAuth.HashSecret("s3cret"), true);
        factory.Stores.Devices.Members.Add(("t1", "staff-1"));
    }

    private HttpClient UserClient(string tenantId = "t1", string userId = "staff-1")
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", SafetyApiFactory.UserToken(tenantId, userId));
        return client;
    }

    private static object IngestBody(string edgeEventId = "edge-evt-1") => new
    {
        edgeEventId,
        campusId = "campus-a",
        eventType = "ZoneIntrusion",
        cameraId = "cam-1",
        detectedAtUtc = DateTimeOffset.UtcNow,
        ruleVersion = "rules-3",
        modelVersion = "model-7",
    };

    private static async Task<Guid> IngestAsync(HttpClient ingestClient, string edgeEventId)
    {
        var response = await ingestClient.PostAsJsonAsync("/v1/ingest/events", IngestBody(edgeEventId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        return doc.GetProperty("id").GetGuid();
    }

    private HttpClient DeviceClient(string deviceHeader)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(DeviceAuth.HeaderName, deviceHeader);
        return client;
    }

    [Fact]
    public async Task Double_ingest_returns_single_id()
    {
        var client = DeviceClient(SafetyApiFactory.DeviceHeader("edge-1", "s3cret"));
        var first = await IngestAsync(client, "edge-dup-1");
        var second = await IngestAsync(client, "edge-dup-1");
        Assert.Equal(first, second);
        Assert.NotEqual(Guid.Empty, first);
        Assert.Single(_factory.Stores.Incidents.Outbox, e => e.Payload.Contains("edge-dup-1"));
    }

    [Fact]
    public async Task Ingest_broadcasts_created_once_for_duplicates()
    {
        var client = DeviceClient(SafetyApiFactory.DeviceHeader("edge-1", "s3cret"));
        var before = _factory.Stores.Broadcast.Calls.Count;
        await IngestAsync(client, "edge-bc-1");
        await IngestAsync(client, "edge-bc-1");
        Assert.Single(
            _factory.Stores.Broadcast.Calls.Skip(before),
            c => c.Method == "incident.created");
    }

    [Fact]
    public async Task Revoked_device_returns_401()
    {
        var client = DeviceClient(SafetyApiFactory.DeviceHeader("edge-revoked", "s3cret"));
        var response = await client.PostAsJsonAsync("/v1/ingest/events", IngestBody());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_device_returns_401()
    {
        var client = DeviceClient(SafetyApiFactory.DeviceHeader("nope", "whatever"));
        var response = await client.PostAsJsonAsync("/v1/ingest/events", IngestBody());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_secret_returns_401()
    {
        var client = DeviceClient(SafetyApiFactory.DeviceHeader("edge-1", "wrong"));
        var response = await client.PostAsJsonAsync("/v1/ingest/events", IngestBody());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Missing_device_header_returns_401()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/v1/ingest/events", IngestBody());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ingest_validation_failure_returns_400()
    {
        var client = DeviceClient(SafetyApiFactory.DeviceHeader("edge-1", "s3cret"));
        var bad = new
        {
            edgeEventId = "",
            campusId = "campus-a",
            eventType = "ZoneIntrusion",
            cameraId = "cam-1",
            detectedAtUtc = DateTimeOffset.UtcNow,
            ruleVersion = "rules-3",
            modelVersion = "model-7",
        };
        var response = await client.PostAsJsonAsync("/v1/ingest/events", bad);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation.failed", doc.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Stale_if_match_returns_409()
    {
        var device = DeviceClient(SafetyApiFactory.DeviceHeader("edge-1", "s3cret"));
        var id = await IngestAsync(device, "edge-stale-1");
        var user = UserClient();
        using var ack = new HttpRequestMessage(new HttpMethod("PATCH"), $"/v1/incidents/{id}");
        ack.Headers.TryAddWithoutValidation("If-Match", "W/\"0\"");
        ack.Content = JsonContent.Create(new { next = "Acknowledged" });
        var acked = await user.SendAsync(ack);
        Assert.Equal(HttpStatusCode.OK, acked.StatusCode);

        using var stale = new HttpRequestMessage(new HttpMethod("PATCH"), $"/v1/incidents/{id}");
        stale.Headers.TryAddWithoutValidation("If-Match", "W/\"0\"");
        stale.Content = JsonContent.Create(new { next = "Investigating" });
        var response = await user.SendAsync(stale);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("incident.concurrency_conflict", doc.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Missing_if_match_returns_428()
    {
        var device = DeviceClient(SafetyApiFactory.DeviceHeader("edge-1", "s3cret"));
        var id = await IngestAsync(device, "edge-428-1");
        var user = UserClient();
        var response = await user.PatchAsJsonAsync($"/v1/incidents/{id}", new { next = "Acknowledged" });
        Assert.Equal((HttpStatusCode)428, response.StatusCode);
    }

    [Fact]
    public async Task Illegal_transition_returns_422()
    {
        var device = DeviceClient(SafetyApiFactory.DeviceHeader("edge-1", "s3cret"));
        var id = await IngestAsync(device, "edge-422-1");
        var user = UserClient();
        using var request = new HttpRequestMessage(new HttpMethod("PATCH"), $"/v1/incidents/{id}");
        request.Headers.TryAddWithoutValidation("If-Match", "W/\"0\"");
        request.Content = JsonContent.Create(new { next = "Resolved" });
        var response = await user.SendAsync(request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("incident.invalid_transition", doc.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Cross_tenant_read_returns_404()
    {
        var device = DeviceClient(SafetyApiFactory.DeviceHeader("edge-1", "s3cret"));
        var id = await IngestAsync(device, "edge-x-1");
        var other = UserClient("t2", "staff-2");
        _factory.Stores.Devices.Members.Add(("t2", "staff-2"));
        var response = await other.GetAsync($"/v1/incidents/{id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Non_member_returns_403()
    {
        var outsider = UserClient("t1", "stranger");
        var response = await outsider.GetAsync("/v1/incidents");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_user_endpoints_return_401()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/v1/incidents");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_incident_returns_etag_and_patch_advances_it()
    {
        var device = DeviceClient(SafetyApiFactory.DeviceHeader("edge-1", "s3cret"));
        var id = await IngestAsync(device, "edge-etag-1");
        var user = UserClient();
        var got = await user.GetAsync($"/v1/incidents/{id}");
        Assert.Equal(HttpStatusCode.OK, got.StatusCode);
        Assert.Equal("W/\"0\"", got.Headers.ETag?.ToString());

        using var patch = new HttpRequestMessage(new HttpMethod("PATCH"), $"/v1/incidents/{id}");
        patch.Headers.TryAddWithoutValidation("If-Match", "W/\"0\"");
        patch.Content = JsonContent.Create(new { next = "Acknowledged" });
        var patched = await user.SendAsync(patch);
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        Assert.Equal("W/\"1\"", patched.Headers.ETag?.ToString());

        Assert.Contains(
            _factory.Stores.Broadcast.Calls,
            c => c.Method == "incident.updated" && c.TenantId == "t1" && c.CampusId == "campus-a");
    }

    [Fact]
    public async Task Dead_letter_surfaces_parked_rows()
    {
        _factory.Stores.Incidents.DeadLetters.Add(new DeadLetterRow(
            Guid.NewGuid(), "t1", "incident.created", "{\"incidentId\":\"x\"}", 10, DateTimeOffset.UtcNow));
        var user = UserClient();
        var response = await user.GetAsync("/v1/ops/dead-letter");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(doc.GetProperty("items").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Dev_stub_mints_usable_token()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/v1/auth/dev-token", new { tenantId = "t1", userId = "staff-1" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = doc.GetProperty("accessToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));

        var authed = _factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var list = await authed.GetAsync("/v1/incidents");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }
}
