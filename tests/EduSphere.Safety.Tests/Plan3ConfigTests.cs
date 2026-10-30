using EduSphere.Safety.Application;
using Xunit;

public sealed class Plan3ConfigTests
{
    private const string ValidBundle = """
        {"format":"edusphere-rule-bundle/1","bundleVersion":1,"modelVersion":"sim-detector/1",
         "schedules":[{"scheduleId":"night","ianaTimezone":"UTC","closedWindows":[{"start":"00:00","end":"00:00"}]}],
         "rules":[{"ruleId":"zone-a","type":"ZoneIntrusion","version":1,
           "confidenceThreshold":0.5,"persistenceFrames":3,"cooldownSeconds":3600,
           "polygon":[[0.35,0.0],[0.65,0.0],[0.65,1.0],[0.35,1.0]],
           "scheduleId":"night"}]}
        """;

    [Fact]
    public async Task PublishBundle_Valid_BumpsVersionOnce()
    {
        var store = new FakeConfigStore();
        var handler = new PublishBundleHandler(store);
        var v1 = await handler.HandleAsync(new PublishBundleCommand("t1", ValidBundle, "staff-1"), CancellationToken.None);
        var v2 = await handler.HandleAsync(new PublishBundleCommand("t1", ValidBundle, "staff-1"), CancellationToken.None);

        Assert.True(v1.IsSuccess && v2.IsSuccess);
        Assert.Equal(1, v1.Value);
        Assert.Equal(2, v2.Value);
        Assert.Equal(2, await store.GetVersionAsync("t1", CancellationToken.None));
    }

    [Fact]
    public async Task PublishBundle_MalformedJson_Rejected()
    {
        var result = await new PublishBundleHandler(new FakeConfigStore()).HandleAsync(
            new PublishBundleCommand("t1", "{oops", "staff-1"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("validation.failed", result.Error!.Code);
    }

    [Fact]
    public async Task PublishBundle_WrongFormat_Rejected()
    {
        var doc = ValidBundle.Replace("edusphere-rule-bundle/1", "edusphere-rule-bundle/0");
        var result = await new PublishBundleHandler(new FakeConfigStore()).HandleAsync(
            new PublishBundleCommand("t1", doc, "staff-1"), CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task PublishBundle_DanglingScheduleId_Rejected()
    {
        var doc = ValidBundle.Replace("\"scheduleId\":\"night\"}]}", "\"scheduleId\":\"dawn\"}]}");
        var result = await new PublishBundleHandler(new FakeConfigStore()).HandleAsync(
            new PublishBundleCommand("t1", doc, "staff-1"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("validation.failed", result.Error!.Code);
    }

    [Fact]
    public async Task PublishBundle_DuplicateScheduleId_Rejected()
    {
        var doc = ValidBundle.Replace(
            "\"schedules\":[{",
            "\"schedules\":[{\"scheduleId\":\"night\",\"ianaTimezone\":\"UTC\",\"closedWindows\":[]},{");
        var result = await new PublishBundleHandler(new FakeConfigStore()).HandleAsync(
            new PublishBundleCommand("t1", doc, "staff-1"), CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task GetActiveConfig_ReturnsLatestSnapshot()
    {
        var store = new FakeConfigStore();
        await new PublishBundleHandler(store).HandleAsync(
            new PublishBundleCommand("t1", ValidBundle, "staff-1"), CancellationToken.None);
        await new SetPolicyHandler(store).HandleAsync(
            new SetPolicyCommand("t1", "default", "[{\"afterSeconds\":300}]", "[\"ops@school\"]", "staff-1"),
            CancellationToken.None);

        var config = await store.GetActiveConfigAsync("t1", CancellationToken.None);

        Assert.NotNull(config);
        Assert.Equal(1, config.BundleVersion);
        Assert.Contains("zone-a", config.BundleDocument);
        var policy = Assert.Single(config.Policies);
        Assert.Equal("default", policy.Id);
    }

    [Fact]
    public async Task GetActiveConfig_UnknownTenant_Null()
    {
        var config = await new FakeConfigStore().GetActiveConfigAsync("nope", CancellationToken.None);
        Assert.Null(config);
    }

    [Fact]
    public async Task SetPolicy_BadLevels_Rejected()
    {
        var result = await new SetPolicyHandler(new FakeConfigStore()).HandleAsync(
            new SetPolicyCommand("t1", "default", "{}", "[\"ops@school\"]", "staff-1"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("validation.failed", result.Error!.Code);
    }

    private sealed class FakeConfigStore : IConfigStore
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

            var version = list.Count + 1;
            list.Add(new BundleRecord(tenantId, version, document, createdBy, DateTimeOffset.UtcNow));
            return Task.FromResult(version);
        }

        public Task<int> GetVersionAsync(string tenantId, CancellationToken ct) =>
            Task.FromResult(_bundles.TryGetValue(tenantId, out var list) ? list.Count : 0);

        public Task<BundleRecord?> GetLatestBundleAsync(string tenantId, CancellationToken ct) =>
            Task.FromResult(_bundles.TryGetValue(tenantId, out var list) && list.Count > 0 ? list[^1] : null);

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
            return new ActiveConfig(
                tenantId, bundle.Version, bundle.Document, await ListPoliciesAsync(tenantId, ct));
        }
    }
}
