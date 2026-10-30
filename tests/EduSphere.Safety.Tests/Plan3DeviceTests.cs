using EduSphere.Safety.Application;
using EduSphere.Safety.Shared;
using Xunit;

public sealed class Plan3DeviceTests
{
    // ---- validators ----

    [Fact]
    public void CreateEnrolCode_EmptyTenant_Invalid()
    {
        var result = new CreateEnrolCodeCommandValidator().Validate(
            new CreateEnrolCodeCommand(string.Empty, null, "u1"));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void CreateEnrolCode_WellFormed_Valid()
    {
        var result = new CreateEnrolCodeCommandValidator().Validate(
            new CreateEnrolCodeCommand("t1", "campus-a", "u1"));
        Assert.True(result.IsValid);
    }

    [Fact]
    public void EnrolDevice_EmptyCode_Invalid()
    {
        var result = new EnrolDeviceCommandValidator().Validate(new EnrolDeviceCommand(" "));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void RotateDeviceSecret_EmptyIds_Invalid()
    {
        var result = new RotateDeviceSecretCommandValidator().Validate(
            new RotateDeviceSecretCommand(string.Empty, string.Empty));
        Assert.False(result.IsValid);
    }

    // ---- handlers (fake store with atomic single-use semantics) ----

    [Fact]
    public async Task CreateEnrolCode_ReturnsCodeAndStoresHash()
    {
        var store = new FakeDeviceStore();
        var handler = new CreateEnrolCodeHandler(store);
        var before = DateTimeOffset.UtcNow;
        var result = await handler.HandleAsync(
            new CreateEnrolCodeCommand("t1", "campus-a", "staff-1"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(result.Value!));
        var grant = await store.ConsumeEnrolCodeAsync(
            Secrets.Sha256Hex(result.Value!), DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.NotNull(grant);
        Assert.Equal("t1", grant.TenantId);
        Assert.Equal("campus-a", grant.CampusId);
        Assert.True(store.LastCodeExpiry - before >= TimeSpan.FromMinutes(14));
        Assert.True(store.LastCodeExpiry - before <= TimeSpan.FromMinutes(16));
    }

    [Fact]
    public async Task EnrolDevice_ValidCode_RegistersDeviceAndReturnsSecretOnce()
    {
        var store = new FakeDeviceStore();
        var created = await new CreateEnrolCodeHandler(store).HandleAsync(
            new CreateEnrolCodeCommand("t1", null, "staff-1"), CancellationToken.None);
        Assert.True(created.IsSuccess);
        var enrolled = await new EnrolDeviceHandler(store).HandleAsync(
            new EnrolDeviceCommand(created.Value!), CancellationToken.None);

        Assert.True(enrolled.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(enrolled.Value!.DeviceId));
        Assert.False(string.IsNullOrWhiteSpace(enrolled.Value!.Secret));
        var registered = await store.FindByIdAsync(enrolled.Value!.DeviceId, CancellationToken.None);
        Assert.NotNull(registered);
        Assert.Equal("t1", registered.TenantId);
        Assert.Equal(Secrets.Sha256Hex(enrolled.Value!.Secret), registered.KeyHash);
        Assert.False(registered.Revoked);
    }

    [Fact]
    public async Task EnrolDevice_SameCodeTwice_SecondFails()
    {
        var store = new FakeDeviceStore();
        var created = await new CreateEnrolCodeHandler(store).HandleAsync(
            new CreateEnrolCodeCommand("t1", null, "staff-1"), CancellationToken.None);
        var first = await new EnrolDeviceHandler(store).HandleAsync(
            new EnrolDeviceCommand(created.Value!), CancellationToken.None);
        var second = await new EnrolDeviceHandler(store).HandleAsync(
            new EnrolDeviceCommand(created.Value!), CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.False(second.IsSuccess);
        Assert.Equal("device.enrol_invalid", second.Error!.Code);
    }

    [Fact]
    public async Task EnrolDevice_UnknownCode_Fails()
    {
        var result = await new EnrolDeviceHandler(new FakeDeviceStore()).HandleAsync(
            new EnrolDeviceCommand("not-a-real-code"), CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("device.enrol_invalid", result.Error!.Code);
    }

    [Fact]
    public async Task RotateDeviceSecret_ReplacesHashAndOldSecretDies()
    {
        var store = new FakeDeviceStore();
        var created = await new CreateEnrolCodeHandler(store).HandleAsync(
            new CreateEnrolCodeCommand("t1", null, "staff-1"), CancellationToken.None);
        var enrolled = await new EnrolDeviceHandler(store).HandleAsync(
            new EnrolDeviceCommand(created.Value!), CancellationToken.None);
        var oldHash = (await store.FindByIdAsync(enrolled.Value!.DeviceId, CancellationToken.None))!.KeyHash;

        var rotated = await new RotateDeviceSecretHandler(store).HandleAsync(
            new RotateDeviceSecretCommand("t1", enrolled.Value!.DeviceId), CancellationToken.None);

        Assert.True(rotated.IsSuccess);
        Assert.NotEqual(enrolled.Value!.Secret, rotated.Value!);
        var current = await store.FindByIdAsync(enrolled.Value!.DeviceId, CancellationToken.None);
        Assert.Equal(Secrets.Sha256Hex(rotated.Value!), current!.KeyHash);
        Assert.NotEqual(oldHash, current.KeyHash);
    }

    [Fact]
    public async Task RotateDeviceSecret_UnknownDevice_Fails()
    {
        var result = await new RotateDeviceSecretHandler(new FakeDeviceStore()).HandleAsync(
            new RotateDeviceSecretCommand("t1", "dev-nope"), CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("device.not_found", result.Error!.Code);
    }

    [Fact]
    public async Task RevokeDevice_TogglesRevoked()
    {
        var store = new FakeDeviceStore();
        var created = await new CreateEnrolCodeHandler(store).HandleAsync(
            new CreateEnrolCodeCommand("t1", null, "staff-1"), CancellationToken.None);
        var enrolled = await new EnrolDeviceHandler(store).HandleAsync(
            new EnrolDeviceCommand(created.Value!), CancellationToken.None);

        var revoked = await new RevokeDeviceHandler(store).HandleAsync(
            new RevokeDeviceCommand("t1", enrolled.Value!.DeviceId, true), CancellationToken.None);
        Assert.True(revoked.IsSuccess);
        Assert.True((await store.FindByIdAsync(enrolled.Value!.DeviceId, CancellationToken.None))!.Revoked);

        var unrevoked = await new RevokeDeviceHandler(store).HandleAsync(
            new RevokeDeviceCommand("t1", enrolled.Value!.DeviceId, false), CancellationToken.None);
        Assert.True(unrevoked.IsSuccess);
        Assert.False((await store.FindByIdAsync(enrolled.Value!.DeviceId, CancellationToken.None))!.Revoked);
    }

    [Fact]
    public async Task RevokeDevice_UnknownDevice_Fails()
    {
        var result = await new RevokeDeviceHandler(new FakeDeviceStore()).HandleAsync(
            new RevokeDeviceCommand("t1", "dev-nope", true), CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("device.not_found", result.Error!.Code);
    }

    private sealed class FakeDeviceStore : IDeviceStore
    {
        private readonly Dictionary<string, DeviceRecord> _devices = new();
        private readonly Dictionary<string, (string TenantId, string? CampusId, DateTimeOffset Expires, bool Used)> _codes = new();

        public DateTimeOffset LastCodeExpiry { get; private set; }

        public Task<DeviceRecord?> FindByIdAsync(string deviceId, CancellationToken ct) =>
            Task.FromResult(_devices.TryGetValue(deviceId, out var d) ? d : null);

        public Task<bool> IsTenantMemberAsync(string tenantId, string userId, CancellationToken ct) =>
            Task.FromResult(true);

        public Task CreateEnrolCodeAsync(
            string tenantId, string? campusId, string codeHash,
            DateTimeOffset expiresAt, string createdBy, CancellationToken ct)
        {
            _codes[codeHash] = (tenantId, campusId, expiresAt, false);
            LastCodeExpiry = expiresAt;
            return Task.CompletedTask;
        }

        public Task<EnrolGrant?> ConsumeEnrolCodeAsync(string codeHash, DateTimeOffset nowUtc, CancellationToken ct)
        {
            if (!_codes.TryGetValue(codeHash, out var c) || c.Used || c.Expires <= nowUtc)
                return Task.FromResult<EnrolGrant?>(null);
            _codes[codeHash] = (c.TenantId, c.CampusId, c.Expires, true);
            return Task.FromResult<EnrolGrant?>(new EnrolGrant(c.TenantId, c.CampusId));
        }

        public Task RegisterDeviceAsync(DeviceRecord device, CancellationToken ct)
        {
            _devices[device.DeviceId] = device;
            return Task.CompletedTask;
        }

        public Task<bool> RotateSecretAsync(string tenantId, string deviceId, string newKeyHash, CancellationToken ct)
        {
            if (!_devices.TryGetValue(deviceId, out var d) || d.TenantId != tenantId)
                return Task.FromResult(false);
            _devices[deviceId] = d with { KeyHash = newKeyHash };
            return Task.FromResult(true);
        }

        public Task<bool> SetRevokedAsync(string tenantId, string deviceId, bool revoked, CancellationToken ct)
        {
            if (!_devices.TryGetValue(deviceId, out var d) || d.TenantId != tenantId)
                return Task.FromResult(false);
            _devices[deviceId] = d with { Revoked = revoked };
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<DeviceRecord>> ListAsync(string tenantId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DeviceRecord>>(
                _devices.Values.Where(d => d.TenantId == tenantId).ToList());

        public Task RecordHeartbeatAsync(
            string tenantId, string deviceId,
            string softwareVersion, string modelVersion, int configVersion,
            CancellationToken ct)
        {
            if (_devices.TryGetValue(deviceId, out var d) && d.TenantId == tenantId)
            {
                _devices[deviceId] = d with
                {
                    SoftwareVersion = softwareVersion,
                    ModelVersion = modelVersion,
                    ConfigVersion = configVersion,
                    LastHeartbeatAt = DateTimeOffset.UtcNow,
                };
            }

            return Task.CompletedTask;
        }
    }
}
