using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EduSphere.Safety.Api;
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
