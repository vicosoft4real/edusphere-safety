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
