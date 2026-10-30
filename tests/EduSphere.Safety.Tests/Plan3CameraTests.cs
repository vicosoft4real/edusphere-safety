using EduSphere.Safety.Application;
using EduSphere.Safety.Shared;
using Xunit;

public sealed class Plan3CameraTests
{
    private static CreateCameraCommand Valid(string tenant = "t1") =>
        new(tenant, "campus-a", null, "manual", "192.168.1.20", 554, "/stream");

    [Fact]
    public void CreateCamera_EmptyFields_Invalid()
    {
        var result = new CreateCameraCommandValidator().Validate(
            new CreateCameraCommand(string.Empty, string.Empty, null, "bogus", string.Empty, 0, "no-slash"));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void CreateCamera_WellFormed_Valid()
    {
        var result = new CreateCameraCommandValidator().Validate(Valid());
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task CreateCamera_Valid_StoresAndReturnsId()
    {
        var stores = new FakeCameraStores();
        var result = await new CreateCameraHandler(stores.Cameras, stores.Devices).HandleAsync(
            Valid(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.StartsWith("cam-", result.Value);
        var saved = await stores.Cameras.FindAsync("t1", result.Value!, CancellationToken.None);
        Assert.NotNull(saved);
        Assert.Equal("192.168.1.20", saved.Host);
        Assert.Equal(554, saved.Port);
        Assert.Null(saved.LastTestOk);
    }

    [Fact]
    public async Task CreateCamera_PublicHost_Rejected()
    {
        var stores = new FakeCameraStores();
        var cmd = Valid() with { Host = "8.8.8.8" };
        var result = await new CreateCameraHandler(stores.Cameras, stores.Devices).HandleAsync(
            cmd, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("validation.failed", result.Error!.Code);
    }

    [Fact]
    public async Task CreateCamera_UserinfoInHost_Rejected()
    {
        var stores = new FakeCameraStores();
        var cmd = Valid() with { Host = "ops:pw@192.168.1.20" };
        var result = await new CreateCameraHandler(stores.Cameras, stores.Devices).HandleAsync(
            cmd, CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task CreateCamera_UnknownDevice_Rejected()
    {
        var stores = new FakeCameraStores();
        var cmd = Valid() with { DeviceId = "dev-nope" };
        var result = await new CreateCameraHandler(stores.Cameras, stores.Devices).HandleAsync(
            cmd, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("device.not_found", result.Error!.Code);
    }

    [Fact]
    public async Task ReplaceCamera_UpdatesDescriptor()
    {
        var stores = new FakeCameraStores();
        var created = await new CreateCameraHandler(stores.Cameras, stores.Devices).HandleAsync(
            Valid(), CancellationToken.None);
        Assert.True(created.IsSuccess);

        var replaced = await new ReplaceCameraHandler(stores.Cameras).HandleAsync(
            new ReplaceCameraCommand("t1", created.Value!, "campus-b", null, "onvif", "10.0.0.9", 8554, "/live"),
            CancellationToken.None);

        Assert.True(replaced.IsSuccess);
        var saved = await stores.Cameras.FindAsync("t1", created.Value!, CancellationToken.None);
        Assert.Equal("campus-b", saved!.CampusId);
        Assert.Equal("onvif", saved.Mode);
    }

    [Fact]
    public async Task ReplaceCamera_UnknownId_NotFound()
    {
        var stores = new FakeCameraStores();
        var result = await new ReplaceCameraHandler(stores.Cameras).HandleAsync(
            new ReplaceCameraCommand("t1", "cam-nope", "campus-a", null, "manual", "10.0.0.9", 554, "/x"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("camera.not_found", result.Error!.Code);
    }

    [Fact]
    public async Task ReportCameraTest_RecordsResult()
    {
        var stores = new FakeCameraStores();
        var created = await new CreateCameraHandler(stores.Cameras, stores.Devices).HandleAsync(
            Valid(), CancellationToken.None);
        Assert.True(created.IsSuccess);

        var reported = await new ReportCameraTestHandler(stores.Cameras).HandleAsync(
            new ReportCameraTestCommand("t1", created.Value!, true, "edge-ok"),
            CancellationToken.None);

        Assert.True(reported.IsSuccess);
        var saved = await stores.Cameras.FindAsync("t1", created.Value!, CancellationToken.None);
        Assert.True(saved!.LastTestOk);
        Assert.Equal("edge-ok", saved.LastTestDetail);
        Assert.NotNull(saved.LastTestAt);
    }

    [Fact]
    public async Task DeleteCamera_RemovesRecord()
    {
        var stores = new FakeCameraStores();
        var created = await new CreateCameraHandler(stores.Cameras, stores.Devices).HandleAsync(
            Valid(), CancellationToken.None);
        Assert.True(created.IsSuccess);

        var deleted = await new DeleteCameraHandler(stores.Cameras).HandleAsync(
            new DeleteCameraCommand("t1", created.Value!), CancellationToken.None);

        Assert.True(deleted.IsSuccess);
        Assert.Null(await stores.Cameras.FindAsync("t1", created.Value!, CancellationToken.None));
    }

    private sealed class FakeCameraStores
    {
        public FakeCameraStore Cameras { get; } = new();
        public Plan3FakeDevices Devices { get; } = new();
    }

    private sealed class FakeCameraStore : ICameraStore
    {
        private readonly Dictionary<string, CameraRecord> _cameras = new();

        public Task InsertAsync(CameraRecord camera, CancellationToken ct)
        {
            _cameras[(camera.TenantId, camera.CameraId).ToString()] = camera;
            return Task.CompletedTask;
        }

        public Task<bool> ReplaceAsync(CameraRecord camera, CancellationToken ct)
        {
            var key = (camera.TenantId, camera.CameraId).ToString();
            if (!_cameras.ContainsKey(key))
                return Task.FromResult(false);
            _cameras[key] = camera;
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAsync(string tenantId, string cameraId, CancellationToken ct) =>
            Task.FromResult(_cameras.Remove((tenantId, cameraId).ToString()));

        public Task<CameraRecord?> FindAsync(string tenantId, string cameraId, CancellationToken ct) =>
            Task.FromResult(_cameras.TryGetValue((tenantId, cameraId).ToString(), out var c) ? c : null);

        public Task<IReadOnlyList<CameraRecord>> ListAsync(string tenantId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CameraRecord>>(
                _cameras.Values.Where(c => c.TenantId == tenantId).OrderBy(c => c.CameraId).ToList());

        public Task<bool> ReportTestAsync(
            string tenantId, string cameraId, bool ok, string detail, DateTimeOffset at, CancellationToken ct)
        {
            var key = (tenantId, cameraId).ToString();
            if (!_cameras.TryGetValue(key, out var c))
                return Task.FromResult(false);
            _cameras[key] = c with { LastTestOk = ok, LastTestDetail = detail, LastTestAt = at };
            return Task.FromResult(true);
        }
    }

    private sealed class Plan3FakeDevices : IDeviceStore
    {
        public Task<DeviceRecord?> FindByIdAsync(string deviceId, CancellationToken ct) =>
            Task.FromResult<DeviceRecord?>(null);

        public Task<bool> IsTenantMemberAsync(string tenantId, string userId, CancellationToken ct) =>
            Task.FromResult(true);

        public Task CreateEnrolCodeAsync(
            string tenantId, string? campusId, string codeHash,
            DateTimeOffset expiresAt, string createdBy, CancellationToken ct) => Task.CompletedTask;

        public Task<EnrolGrant?> ConsumeEnrolCodeAsync(string codeHash, DateTimeOffset nowUtc, CancellationToken ct) =>
            Task.FromResult<EnrolGrant?>(null);

        public Task RegisterDeviceAsync(DeviceRecord device, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> RotateSecretAsync(string tenantId, string deviceId, string newKeyHash, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<bool> SetRevokedAsync(string tenantId, string deviceId, bool revoked, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<DeviceRecord>> ListAsync(string tenantId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DeviceRecord>>(Array.Empty<DeviceRecord>());
    }
}
