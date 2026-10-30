using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EduSphere.Safety.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

public sealed class Plan3ApiTests : IClassFixture<SafetyApiFactory>
{
    private readonly SafetyApiFactory _factory;

    public Plan3ApiTests(SafetyApiFactory factory)
    {
        _factory = factory;
        factory.Stores.Reset();
        factory.Stores.Devices.Members.Add(("t1", "staff-1"));
    }

    private HttpClient UserClient(string tenantId = "t1", string userId = "staff-1")
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", SafetyApiFactory.UserToken(tenantId, userId));
        return client;
    }

    private static HttpClient AnonymousClient(SafetyApiFactory factory) => factory.CreateClient();

    private HttpClient DeviceClient(string deviceHeader)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(DeviceAuth.HeaderName, deviceHeader);
        return client;
    }

    private async Task<(string DeviceId, string Secret)> EnrolDeviceAsync(
        HttpClient staff, string tenantCampusBody = """{"campusId":"campus-a"}""")
    {
        var codeResponse = await staff.PostAsJsonAsync(
            "/v1/devices/enrol-codes", JsonSerializer.Deserialize<JsonElement>(tenantCampusBody));
        Assert.Equal(HttpStatusCode.OK, codeResponse.StatusCode);
        var codeDoc = await codeResponse.Content.ReadFromJsonAsync<JsonElement>();
        var code = codeDoc.GetProperty("code").GetString()!;

        var enrolResponse = await AnonymousClient(_factory).PostAsJsonAsync(
            "/v1/devices/enrol", new { code });
        Assert.Equal(HttpStatusCode.OK, enrolResponse.StatusCode);
        var enrolDoc = await enrolResponse.Content.ReadFromJsonAsync<JsonElement>();
        return (enrolDoc.GetProperty("deviceId").GetString()!, enrolDoc.GetProperty("secret").GetString()!);
    }

    private static object IngestBody(string edgeEventId) => new
    {
        edgeEventId,
        campusId = "campus-a",
        eventType = "ZoneIntrusion",
        cameraId = "cam-1",
        detectedAtUtc = DateTimeOffset.UtcNow,
        ruleVersion = "rules-3",
        modelVersion = "model-7",
    };

    [Fact]
    public async Task EnrolCode_RequiresStaffAuth()
    {
        var response = await AnonymousClient(_factory).PostAsJsonAsync(
            "/v1/devices/enrol-codes", new { campusId = "campus-a" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Enrol_FullCycle_IngestSucceeds()
    {
        var (deviceId, secret) = await EnrolDeviceAsync(UserClient());

        var ingest = await DeviceClient($"{deviceId}.{secret}")
            .PostAsJsonAsync("/v1/ingest/events", IngestBody("evt-enrol-1"));
        Assert.Equal(HttpStatusCode.OK, ingest.StatusCode);
    }

    [Fact]
    public async Task Enrol_SameCodeTwice_SecondFails()
    {
        var staff = UserClient();
        var codeResponse = await staff.PostAsJsonAsync("/v1/devices/enrol-codes", new { });
        var code = (await codeResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

        var first = await AnonymousClient(_factory).PostAsJsonAsync("/v1/devices/enrol", new { code });
        var second = await AnonymousClient(_factory).PostAsJsonAsync("/v1/devices/enrol", new { code });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task Enrol_UnknownCode_Fails()
    {
        var response = await AnonymousClient(_factory).PostAsJsonAsync(
            "/v1/devices/enrol", new { code = "not-a-real-code" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Rotate_OldSecretDiesNewSecretWorks()
    {
        var (deviceId, secret) = await EnrolDeviceAsync(UserClient());
        var rotate = await DeviceClient($"{deviceId}.{secret}").PostAsync("/v1/devices/rotate", null);
        Assert.Equal(HttpStatusCode.OK, rotate.StatusCode);
        var newSecret = (await rotate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;

        var oldIngest = await DeviceClient($"{deviceId}.{secret}")
            .PostAsJsonAsync("/v1/ingest/events", IngestBody("evt-rot-old"));
        var newIngest = await DeviceClient($"{deviceId}.{newSecret}")
            .PostAsJsonAsync("/v1/ingest/events", IngestBody("evt-rot-new"));

        Assert.Equal(HttpStatusCode.Unauthorized, oldIngest.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newIngest.StatusCode);
    }

    [Fact]
    public async Task Revoke_DeviceLosesIngest()
    {
        var (deviceId, secret) = await EnrolDeviceAsync(UserClient());
        var revoke = await UserClient().PostAsJsonAsync($"/v1/devices/{deviceId}/revoke", new { revoked = true });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        var ingest = await DeviceClient($"{deviceId}.{secret}")
            .PostAsJsonAsync("/v1/ingest/events", IngestBody("evt-revoked"));
        Assert.Equal(HttpStatusCode.Unauthorized, ingest.StatusCode);
    }

    [Fact]
    public async Task Revoke_OtherTenantDevice_NotFound()
    {
        var (deviceId, _) = await EnrolDeviceAsync(UserClient("t1"));
        var other = UserClient("t2", "staff-9");
        _factory.Stores.Devices.Members.Add(("t2", "staff-9"));

        var revoke = await other.PostAsJsonAsync($"/v1/devices/{deviceId}/revoke", new { revoked = true });
        Assert.Equal(HttpStatusCode.NotFound, revoke.StatusCode);
    }

    [Fact]
    public async Task Camera_CreateListReportDiagnostics_Flow()
    {
        var staff = UserClient();
        var create = await staff.PostAsJsonAsync("/v1/cameras", new
        {
            campusId = "campus-a",
            deviceId = (string?)null,
            mode = "manual",
            host = "192.168.1.20",
            port = 554,
            path = "/stream",
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var cameraId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var bad = await staff.PostAsJsonAsync("/v1/cameras", new
        {
            campusId = "campus-a",
            deviceId = (string?)null,
            mode = "manual",
            host = "8.8.8.8",
            port = 554,
            path = "/stream",
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var secretCam = await staff.PostAsJsonAsync("/v1/cameras", new
        {
            campusId = "campus-a",
            deviceId = (string?)null,
            mode = "manual",
            host = "ops:pw@192.168.1.20",
            port = 554,
            path = "/stream",
        });
        Assert.Equal(HttpStatusCode.BadRequest, secretCam.StatusCode);

        var (deviceId, secret) = await EnrolDeviceAsync(UserClient());
        var report = await DeviceClient($"{deviceId}.{secret}").PostAsJsonAsync(
            $"/v1/cameras/{cameraId}/test-report", new { ok = true, detail = "edge-ok" });
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);

        var list = await staff.GetAsync("/v1/cameras");
        var doc = await list.Content.ReadFromJsonAsync<JsonElement>();
        var item = doc.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(cameraId, item.GetProperty("id").GetString());
        Assert.True(item.GetProperty("lastTestOk").GetBoolean());
        var itemJson = item.ToString();
        Assert.DoesNotContain("pw@", itemJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ops:", itemJson, StringComparison.Ordinal);
        Assert.DoesNotContain("@192", itemJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Camera_TestReport_OtherTenantDevice_NotFound()
    {
        var staff = UserClient("t1");
        var create = await staff.PostAsJsonAsync("/v1/cameras", new
        {
            campusId = "campus-a",
            deviceId = (string?)null,
            mode = "manual",
            host = "10.0.0.9",
            port = 554,
            path = "/x",
        });
        var cameraId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        _factory.Stores.Devices.Members.Add(("t2", "staff-9"));
        var (deviceId, secret) = await EnrolDeviceAsync(UserClient("t2", "staff-9"));
        var report = await DeviceClient($"{deviceId}.{secret}").PostAsJsonAsync(
            $"/v1/cameras/{cameraId}/test-report", new { ok = true, detail = "x" });
        Assert.Equal(HttpStatusCode.NotFound, report.StatusCode);
    }

    [Fact]
    public async Task Camera_ReplaceDelete_Flow()
    {
        var staff = UserClient();
        var create = await staff.PostAsJsonAsync("/v1/cameras", new
        {
            campusId = "campus-a",
            deviceId = (string?)null,
            mode = "manual",
            host = "10.0.0.9",
            port = 554,
            path = "/x",
        });
        var cameraId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var replace = await staff.PutAsJsonAsync($"/v1/cameras/{cameraId}", new
        {
            campusId = "campus-b",
            deviceId = (string?)null,
            mode = "onvif",
            host = "10.0.0.10",
            port = 8554,
            path = "/live",
        });
        Assert.Equal(HttpStatusCode.OK, replace.StatusCode);

        var missing = await staff.PutAsJsonAsync("/v1/cameras/cam-nope", new
        {
            campusId = "campus-b",
            deviceId = (string?)null,
            mode = "onvif",
            host = "10.0.0.10",
            port = 8554,
            path = "/live",
        });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var delete = await staff.DeleteAsync($"/v1/cameras/{cameraId}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
    }

    [Fact]
    public async Task ListDevices_TenantScoped()
    {
        await EnrolDeviceAsync(UserClient("t1"));
        var list = await UserClient("t1").GetAsync("/v1/devices");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var doc = await list.Content.ReadFromJsonAsync<JsonElement>();
        var items = doc.GetProperty("items").EnumerateArray().ToList();
        Assert.All(items, i => Assert.Equal("t1", i.GetProperty("tenantId").GetString()));
        Assert.DoesNotContain(items, i => i.TryGetProperty("secret", out _) || i.TryGetProperty("keyHash", out _));
    }
}

public sealed class Plan3ConfigApiTests : IClassFixture<SafetyApiFactory>
{
    private const string BundleV1 = """
        {"format":"edusphere-rule-bundle/1","bundleVersion":1,"modelVersion":"sim-detector/1",
         "schedules":[{"scheduleId":"night","ianaTimezone":"UTC","closedWindows":[]}],
         "rules":[{"ruleId":"zone-a","type":"ZoneIntrusion","version":1,
           "confidenceThreshold":0.5,"persistenceFrames":3,"cooldownSeconds":3600,
           "polygon":[[0.35,0.0],[0.65,0.0],[0.65,1.0],[0.35,1.0]],
           "scheduleId":"night"}]}
        """;

    private const string ZoneBRule = """
        ,{"ruleId":"zone-b","type":"PoolIntrusion","version":1,
          "confidenceThreshold":0.5,"persistenceFrames":3,"cooldownSeconds":60,
          "polygon":[[0.0,0.0],[1.0,0.0],[1.0,0.5],[0.0,0.5]],
          "scheduleId":"night"}
        """;

    private readonly SafetyApiFactory _factory;

    public Plan3ConfigApiTests(SafetyApiFactory factory)
    {
        _factory = factory;
        factory.Stores.Reset();
        factory.Stores.Devices.Members.Add(("t1", "staff-1"));
    }

    private HttpClient UserClient(string tenantId = "t1", string userId = "staff-1")
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", SafetyApiFactory.UserToken(tenantId, userId));
        return client;
    }

    private HttpClient DeviceClient(string deviceHeader, EntityTagHeaderValue? ifNoneMatch = null)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(DeviceAuth.HeaderName, deviceHeader);
        if (ifNoneMatch is not null)
            client.DefaultRequestHeaders.IfNoneMatch.Add(ifNoneMatch);
        return client;
    }

    private async Task<(string DeviceId, string Secret)> EnrolDeviceAsync(HttpClient staff)
    {
        var codeResponse = await staff.PostAsJsonAsync("/v1/devices/enrol-codes", new { campusId = "campus-a" });
        Assert.Equal(HttpStatusCode.OK, codeResponse.StatusCode);
        var code = (await codeResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

        var enrolResponse = await _factory.CreateClient().PostAsJsonAsync("/v1/devices/enrol", new { code });
        Assert.Equal(HttpStatusCode.OK, enrolResponse.StatusCode);
        var enrolDoc = await enrolResponse.Content.ReadFromJsonAsync<JsonElement>();
        return (enrolDoc.GetProperty("deviceId").GetString()!, enrolDoc.GetProperty("secret").GetString()!);
    }

    private async Task<HttpResponseMessage> PublishAsync(HttpClient staff, string document) =>
        await staff.PostAsJsonAsync("/v1/config/bundles", new { document });

    [Fact]
    public async Task ConfigPull_FullCycle_ETagAnd304()
    {
        var staff = UserClient();
        var publish = await PublishAsync(staff, BundleV1);
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        Assert.Equal(1, (await publish.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt32());

        var (deviceId, secret) = await EnrolDeviceAsync(staff);
        var key = $"{deviceId}.{secret}";

        var full = await DeviceClient(key).GetAsync("/v1/devices/config");
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.NotNull(full.Headers.ETag);
        Assert.True(full.Headers.ETag.IsWeak);
        Assert.Equal("\"1\"", full.Headers.ETag.Tag);
        var fullDoc = await full.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, fullDoc.GetProperty("bundleVersion").GetInt32());
        Assert.Single(fullDoc.GetProperty("bundle").GetProperty("rules").EnumerateArray());

        var stale = await DeviceClient(key, full.Headers.ETag).GetAsync("/v1/devices/config");
        Assert.Equal(HttpStatusCode.NotModified, stale.StatusCode);

        var bundleV2 = BundleV1.Replace(
            "\"scheduleId\":\"night\"}]}",
            "\"scheduleId\":\"night\"}" + ZoneBRule.Trim() + "]}");
        var publish2 = await PublishAsync(staff, bundleV2);
        Assert.Equal(2, (await publish2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt32());

        var refreshed = await DeviceClient(key, full.Headers.ETag).GetAsync("/v1/devices/config");
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.Equal("\"2\"", refreshed.Headers.ETag!.Tag);
        var refreshedDoc = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, refreshedDoc.GetProperty("bundleVersion").GetInt32());
        Assert.Contains("zone-b", refreshedDoc.GetProperty("bundle").GetRawText());
    }

    [Fact]
    public async Task PublishBundle_MalformedJson_400()
    {
        var response = await PublishAsync(UserClient(), "{oops");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation.failed", doc.GetProperty("code").GetString());
    }

    [Fact]
    public async Task PublishBundle_DanglingSchedule_400()
    {
        var dangling = BundleV1.Replace("\"scheduleId\":\"night\"}]}", "\"scheduleId\":\"dawn\"}]}");
        var response = await PublishAsync(UserClient(), dangling);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PublishBundle_DuplicateSchedule_400()
    {
        var duplicate = BundleV1.Replace(
            "\"schedules\":[{",
            "\"schedules\":[{\"scheduleId\":\"night\",\"ianaTimezone\":\"UTC\",\"closedWindows\":[]},{");
        var response = await PublishAsync(UserClient(), duplicate);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PublishBundle_NonMember_403()
    {
        var response = await PublishAsync(UserClient("t1", "stranger"), BundleV1);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeviceConfig_NoBundle_404()
    {
        var (deviceId, secret) = await EnrolDeviceAsync(UserClient());
        var response = await DeviceClient($"{deviceId}.{secret}").GetAsync("/v1/devices/config");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeviceConfig_UnknownDevice_401()
    {
        var response = await DeviceClient("dev-nope.deadbeefdeadbeefdeadbeefdeadbeef").GetAsync("/v1/devices/config");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Policy_SetList_RoundTrip()
    {
        var staff = UserClient();
        var set = await staff.PostAsJsonAsync("/v1/config/policies", new
        {
            id = "default",
            levels = new[] { new { afterSeconds = 300 } },
            recipients = new[] { "ops@school" },
        });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        var list = await staff.GetAsync("/v1/config/policies");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var item = Assert.Single((await list.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray());
        Assert.Equal("default", item.GetProperty("id").GetString());
        Assert.Equal(300, item.GetProperty("levels")[0].GetProperty("afterSeconds").GetInt32());
        Assert.Equal("ops@school", item.GetProperty("recipients")[0].GetString());

        var (deviceId, secret) = await EnrolDeviceAsync(staff);
        await PublishAsync(staff, BundleV1);
        var pull = await DeviceClient($"{deviceId}.{secret}").GetAsync("/v1/devices/config");
        var pullDoc = await pull.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("default", pullDoc.GetProperty("policies")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Policy_BadLevels_400()
    {
        var response = await UserClient().PostAsJsonAsync("/v1/config/policies", new
        {
            id = "default",
            levels = new { afterSeconds = 300 },
            recipients = new[] { "ops@school" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

public sealed class Plan3EvidenceApiTests : IClassFixture<SafetyApiFactory>
{
    private static readonly string Sha = new('a', 64);

    private readonly SafetyApiFactory _factory;

    public Plan3EvidenceApiTests(SafetyApiFactory factory)
    {
        _factory = factory;
        factory.Stores.Reset();
        factory.Stores.Devices.Members.Add(("t1", "staff-1"));
    }

    private HttpClient UserClient(string tenantId = "t1", string userId = "staff-1")
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", SafetyApiFactory.UserToken(tenantId, userId));
        return client;
    }

    private HttpClient NoRedirectClient(string tenantId = "t1", string userId = "staff-1")
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", SafetyApiFactory.UserToken(tenantId, userId));
        return client;
    }

    private HttpClient DeviceClient(string deviceHeader)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(DeviceAuth.HeaderName, deviceHeader);
        return client;
    }

    private async Task<(string DeviceId, string Secret)> EnrolDeviceAsync(HttpClient staff)
    {
        var codeResponse = await staff.PostAsJsonAsync("/v1/devices/enrol-codes", new { campusId = "campus-a" });
        Assert.Equal(HttpStatusCode.OK, codeResponse.StatusCode);
        var code = (await codeResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

        var enrolResponse = await _factory.CreateClient().PostAsJsonAsync("/v1/devices/enrol", new { code });
        Assert.Equal(HttpStatusCode.OK, enrolResponse.StatusCode);
        var enrolDoc = await enrolResponse.Content.ReadFromJsonAsync<JsonElement>();
        return (enrolDoc.GetProperty("deviceId").GetString()!, enrolDoc.GetProperty("secret").GetString()!);
    }

    private static object ManifestBody(string edgeEventId = "evt-9", string kind = "clip", string campus = "campus-a") => new
    {
        campusId = campus,
        cameraId = "cam-1",
        edgeEventId,
        kind,
        bytes = 1024L,
        sha256 = Sha,
    };

    [Fact]
    public async Task Manifest_Complete_Playback302_LogsAudit()
    {
        var staff = UserClient();
        var (deviceId, secret) = await EnrolDeviceAsync(staff);
        var device = DeviceClient($"{deviceId}.{secret}");

        var manifest = await device.PostAsJsonAsync("/v1/evidence/manifest", ManifestBody());
        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
        var manifestDoc = await manifest.Content.ReadFromJsonAsync<JsonElement>();
        var objectKey = manifestDoc.GetProperty("objectKey").GetString()!;
        Assert.StartsWith("t1/campus-a/cam-1/", objectKey);
        Assert.EndsWith(".clip.mp4", objectKey);
        Assert.False(string.IsNullOrWhiteSpace(manifestDoc.GetProperty("putUrl").GetString()));

        var complete = await device.PostAsJsonAsync(
            "/v1/evidence/manifest/complete", new { edgeEventId = "evt-9", kind = "clip" });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        var playback = await NoRedirectClient().GetAsync("/v1/evidence/evt-9/clip");
        Assert.Equal(HttpStatusCode.Redirect, playback.StatusCode);
        Assert.NotNull(playback.Headers.Location);
        Assert.Contains(objectKey, playback.Headers.Location.ToString());

        var audit = _factory.Stores.Evidence.AccessLog;
        var row = Assert.Single(audit);
        Assert.Equal("staff-1", row.ActorId);
        Assert.Equal("t1", row.ManifestTenant);
        Assert.Equal(objectKey, row.ObjectKey);
    }

    [Fact]
    public async Task Manifest_BadKind_400()
    {
        var (deviceId, secret) = await EnrolDeviceAsync(UserClient());
        var response = await DeviceClient($"{deviceId}.{secret}")
            .PostAsJsonAsync("/v1/evidence/manifest", ManifestBody(kind: "movie"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Manifest_UnknownDevice_401()
    {
        var response = await DeviceClient("dev-nope.deadbeefdeadbeefdeadbeefdeadbeef")
            .PostAsJsonAsync("/v1/evidence/manifest", ManifestBody());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Manifest_DeviceCampusMismatch_403()
    {
        var (deviceId, secret) = await EnrolDeviceAsync(UserClient());
        var response = await DeviceClient($"{deviceId}.{secret}")
            .PostAsJsonAsync("/v1/evidence/manifest", ManifestBody(campus: "campus-b"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Playback_OtherTenant_404()
    {
        var (deviceId, secret) = await EnrolDeviceAsync(UserClient());
        var manifest = await DeviceClient($"{deviceId}.{secret}")
            .PostAsJsonAsync("/v1/evidence/manifest", ManifestBody());
        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);

        _factory.Stores.Devices.Members.Add(("t2", "staff-9"));
        var playback = await NoRedirectClient("t2", "staff-9").GetAsync("/v1/evidence/evt-9/clip");
        Assert.Equal(HttpStatusCode.NotFound, playback.StatusCode);
        Assert.Empty(_factory.Stores.Evidence.AccessLog);
    }

    [Fact]
    public async Task Complete_UnknownManifest_404()
    {
        var (deviceId, secret) = await EnrolDeviceAsync(UserClient());
        var response = await DeviceClient($"{deviceId}.{secret}")
            .PostAsJsonAsync("/v1/evidence/manifest/complete", new { edgeEventId = "evt-nope", kind = "clip" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
