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

public sealed class SafetyApiFactory : WebApplicationFactory<Program>
{
    public const string SigningKey = "local-dev-only-32-bytes-minimum-key!!";
    public const string Issuer = "edusphere-dev";
    public const string Audience = "edusphere-safety";

    public FakeApiStores Stores { get; } = new();

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("Auth:DevStubEnabled", "true");
        builder.UseSetting("Auth:IssuerSigningKey", SigningKey);
        builder.UseSetting("Auth:Issuer", Issuer);
        builder.UseSetting("Auth:Audience", Audience);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IIncidentStore>(Stores.Incidents);
            services.AddSingleton<IDeviceStore>(Stores.Devices);
            services.AddSingleton<IIncidentBroadcaster>(Stores.Broadcast);
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

    public void Reset()
    {
        Incidents.Reset();
        Devices.Reset();
        Broadcast.Reset();
    }
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

    public Task<DeviceRecord?> FindByIdAsync(string deviceId, CancellationToken ct) =>
        Task.FromResult(Devices.TryGetValue(deviceId, out var d) ? d : null);

    public Task<bool> IsTenantMemberAsync(string tenantId, string userId, CancellationToken ct) =>
        Task.FromResult(Members.Contains((tenantId, userId)));

    public void Reset()
    {
        Devices.Clear();
        Members.Clear();
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
