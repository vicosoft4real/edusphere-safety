using EduSphere.Safety.Application;
using EduSphere.Safety.Core;
using EduSphere.Safety.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

public sealed class Plan3NotificationTests
{
    private static CreateIncidentCommand ValidCreate(string edgeEventId = "edge-n1") => new(
        edgeEventId, "t1", "campus-a", "ZoneIntrusion", "cam-1",
        DateTimeOffset.UtcNow.AddMinutes(-2), "rules-3", "model-7");

    private static string LevelsJson(params int[] delays) =>
        "[" + string.Join(",", delays.Select(d => $"{{\"afterSeconds\":{d}}}")) + "]";

    private sealed class TestConfigStore(Dictionary<string, PolicyRecord> policies) : IConfigStore
    {
        public Task<int> PublishBundleAsync(string tenantId, string document, string createdBy, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<BundleRecord?> GetLatestBundleAsync(string tenantId, CancellationToken ct) =>
            Task.FromResult<BundleRecord?>(null);
        public Task<PolicyRecord?> GetPolicyAsync(string tenantId, string id, CancellationToken ct) =>
            Task.FromResult(policies.TryGetValue(tenantId + "/" + id, out var p) ? p : null);
        public Task SetPolicyAsync(PolicyRecord policy, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<PolicyRecord>> ListPoliciesAsync(string tenantId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PolicyRecord>>([]);
        public Task<ActiveConfig?> GetActiveConfigAsync(string tenantId, CancellationToken ct) =>
            Task.FromResult<ActiveConfig?>(null);
    }

    private static TestConfigStore PolicyStore(params int[] delays) => new(
        new Dictionary<string, PolicyRecord>
        {
            ["t1/default"] = new PolicyRecord(
                "t1", "default", LevelsJson(delays), "[\"ops@school\",\"chief@school\"]",
                "staff-1", DateTimeOffset.UtcNow),
        });

    internal static IConfigStore EmptyPolicyStore() => new TestConfigStore(new Dictionary<string, PolicyRecord>());

    internal sealed class FakeNotificationStore : INotificationStore
    {
        public readonly Dictionary<(string Tenant, Guid Incident, int Level), (DateTimeOffset Due, DateTimeOffset? LockedUntil, int Attempts)> Schedules = new();
        public readonly List<DeliveryAttempt> Attempts = new();
        public readonly Dictionary<(string Tenant, Guid Incident), IncidentState> IncidentStates = new();

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

        public Task<FireDecision> PrepareFireAsync(string tenantId, Guid incidentId, int level, CancellationToken ct)
        {
            if (!IncidentStates.TryGetValue((tenantId, incidentId), out var state) || state != IncidentState.New)
                Schedules.Remove((tenantId, incidentId, level));
            return Task.FromResult(
                IncidentStates.TryGetValue((tenantId, incidentId), out var s) && s == IncidentState.New
                    ? FireDecision.Proceed
                    : FireDecision.Cancelled);
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
    }

    internal sealed class FakeEmailSender : IEmailSender
    {
        public readonly List<(string To, string Subject, string Body)> Sent = new();
        public Func<(string To, string Subject, string Body), CancellationToken, Task>? Behavior;

        public async Task SendAsync(string to, string subject, string body, CancellationToken ct)
        {
            if (Behavior is not null)
                await Behavior((to, subject, body), ct);
            Sent.Add((to, subject, body));
        }
    }

    private sealed class FakeOutboxStore(List<OutboxClaim> rows) : IOutboxDispatcherStore
    {
        public readonly List<OutboxClaim> Rows = rows;
        public readonly HashSet<Guid> Deleted = [];
        public readonly Dictionary<Guid, DateTimeOffset> Locked = new();

        public Task<IReadOnlyList<OutboxClaim>> ClaimDueAsync(int batchSize, DateTimeOffset leaseUntilUtc, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<OutboxClaim>>(Rows.Where(r => !Deleted.Contains(r.Id)).Take(batchSize).ToList());

        public Task DeleteAsync(Guid outboxId, CancellationToken ct)
        {
            Deleted.Add(outboxId);
            return Task.CompletedTask;
        }

        public Task RescheduleAsync(Guid outboxId, DateTimeOffset lockedUntilUtc, CancellationToken ct)
        {
            Locked[outboxId] = lockedUntilUtc;
            return Task.CompletedTask;
        }
    }

    private static ServiceProvider DispatcherServices(
        IIncidentStore incidents,
        INotificationStore notifications,
        IConfigStore config,
        IEmailSender email)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(incidents);
        services.AddSingleton(notifications);
        services.AddSingleton(config);
        services.AddSingleton(email);
        services.AddSingleton(new OutboxOptions());
        services.AddSingleton(new NotificationOptions { To = ["pilot@school"] });
        services.AddSingleton<EscalationDispatcher>();
        services.AddSingleton<OutboxDispatcher>();
        return services.BuildServiceProvider();
    }

    // ---------- creation ----------

    [Fact]
    public async Task Create_SchedulesLevelOne_AtDetectedPlusFirstDelay()
    {
        var incidents = new IncidentHandlerTests.FakeIncidentStore();
        var notifications = new FakeNotificationStore();
        var detected = DateTimeOffset.UtcNow.AddMinutes(-2);
        var cmd = ValidCreate() with { DetectedAtUtc = detected };
        var result = await new CreateIncidentHandler(incidents, notifications, PolicyStore(300))
            .HandleAsync(cmd, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var row = Assert.Single(notifications.Schedules);
        Assert.Equal(1, row.Key.Level);
        Assert.Equal("t1", row.Key.Tenant);
        Assert.Equal(result.Value, row.Key.Incident);
        Assert.Equal(detected.AddSeconds(300), row.Value.Due, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Create_ZeroLevelsPolicy_SchedulesNothing()
    {
        var incidents = new IncidentHandlerTests.FakeIncidentStore();
        var notifications = new FakeNotificationStore();
        var result = await new CreateIncidentHandler(incidents, notifications, PolicyStore())
            .HandleAsync(ValidCreate(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(notifications.Schedules);
    }

    [Fact]
    public async Task Create_DuplicateIngest_DoesNotDuplicateSchedule()
    {
        var incidents = new IncidentHandlerTests.FakeIncidentStore();
        var notifications = new FakeNotificationStore();
        var handler = new CreateIncidentHandler(incidents, notifications, PolicyStore(300));

        var first = await handler.HandleAsync(ValidCreate("edge-dup"), CancellationToken.None);
        var second = await handler.HandleAsync(ValidCreate("edge-dup"), CancellationToken.None);

        Assert.True(first.IsSuccess && second.IsSuccess);
        Assert.Equal(first.Value, second.Value);
        Assert.Single(notifications.Schedules);
    }

    // ---------- routing ----------

    [Fact]
    public void ResolveRecipients_UnionsAndDedups()
    {
        var resolved = NotificationRouting.ResolveRecipients(
            ["ops@school", " extra@school "],
            ["extra@school", "chief@school"],
            ["chief@school", "", "ops@school"]);

        Assert.Equal(["ops@school", "extra@school", "chief@school"], resolved);
    }

    // ---------- dispatcher ----------

    private static async Task<(ServiceProvider Provider, FakeNotificationStore Notifications, FakeEmailSender Email)>
        EscalationSetupAsync(IncidentState state, int level, DateTimeOffset due, params int[] delays)
    {
        var incidents = new IncidentHandlerTests.FakeIncidentStore();
        var notifications = new FakeNotificationStore();
        var incidentId = Guid.NewGuid();
        await incidents.AddAsync(
            Incident.Rehydrate(incidentId, "t1", "campus-a", "ZoneIntrusion", "cam-1", "edge-n1", state, 0),
            new OutboxEntry("t1", "test.seed", "{}"),
            CancellationToken.None);
        notifications.IncidentStates[("t1", incidentId)] = state;
        notifications.Schedules[("t1", incidentId, level)] = (due, null, 0);
        var email = new FakeEmailSender();
        var provider = DispatcherServices(incidents, notifications, PolicyStore(delays), email);
        return (provider, notifications, email);
    }

    [Fact]
    public async Task AckBeforeDue_CancelsSilently_NoSend()
    {
        var (provider, notifications, email) = await EscalationSetupAsync(
            IncidentState.Acknowledged, 1, DateTimeOffset.UtcNow.AddMinutes(-1), 300);
        var dispatcher = provider.GetRequiredService<EscalationDispatcher>();

        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        Assert.Empty(email.Sent);
        Assert.Empty(notifications.Attempts);
        Assert.Empty(notifications.Schedules);
    }

    [Fact]
    public async Task SendFailure_AppendsFailed_AndReschedulesWithBackoff()
    {
        var (provider, notifications, email) = await EscalationSetupAsync(
            IncidentState.New, 1, DateTimeOffset.UtcNow.AddMinutes(-1), 300);
        email.Behavior = (_, _) => throw new InvalidOperationException("smtp down");
        var dispatcher = provider.GetRequiredService<EscalationDispatcher>();

        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        Assert.Equal(2, notifications.Attempts.Count);
        Assert.All(notifications.Attempts, a =>
        {
            Assert.Equal("failed", a.State);
            Assert.Equal("email", a.Channel);
            Assert.Equal(1, a.Level);
        });
        var schedule = Assert.Single(notifications.Schedules);
        Assert.True(schedule.Value.LockedUntil > DateTimeOffset.UtcNow.AddSeconds(10));
    }

    [Fact]
    public async Task SecondLevel_FiresAfterOwnDelay()
    {
        var (provider, notifications, email) = await EscalationSetupAsync(
            IncidentState.New, 1, DateTimeOffset.UtcNow.AddMinutes(-1), 300, 3600);
        var dispatcher = provider.GetRequiredService<EscalationDispatcher>();

        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        Assert.Equal(2, email.Sent.Count);
        var l2 = Assert.Single(notifications.Schedules);
        Assert.Equal(2, l2.Key.Level);
        Assert.True(l2.Value.Due > DateTimeOffset.UtcNow.AddSeconds(3500));

        notifications.Schedules[(l2.Key.Tenant, l2.Key.Incident, 2)] =
            (DateTimeOffset.UtcNow.AddMinutes(-1), null, 0);
        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        Assert.Equal(4, email.Sent.Count);
        Assert.Empty(notifications.Schedules);
    }

    [Fact]
    public async Task Cancellation_RecordsUnknown()
    {
        var (provider, notifications, email) = await EscalationSetupAsync(
            IncidentState.New, 1, DateTimeOffset.UtcNow.AddMinutes(-1), 300);
        using var cts = new CancellationTokenSource();
        email.Behavior = (_, _) =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        var dispatcher = provider.GetRequiredService<EscalationDispatcher>();

        await dispatcher.DispatchOnceAsync(cts.Token);

        Assert.Empty(email.Sent);
        var attempt = Assert.Single(notifications.Attempts);
        Assert.Equal("unknown", attempt.State);
        Assert.Equal("ops@school", attempt.Recipient);
        Assert.Equal(1, attempt.Level);
    }

    [Fact]
    public async Task FullCycle_NeverRecordsDelivered()
    {
        var (provider, notifications, email) = await EscalationSetupAsync(
            IncidentState.New, 1, DateTimeOffset.UtcNow.AddMinutes(-1), 300);
        var dispatcher = provider.GetRequiredService<EscalationDispatcher>();

        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        Assert.NotEmpty(email.Sent);
        Assert.NotEmpty(notifications.Attempts);
        Assert.DoesNotContain(notifications.Attempts, a => a.State == "delivered");
        Assert.All(notifications.Attempts, a =>
            Assert.True(a.State is "accepted" or "failed" or "unknown", $"unexpected state {a.State}"));
    }

    [Fact]
    public async Task OutboxLevel0_RecordsAcceptedLedger()
    {
        var incidentId = Guid.NewGuid();
        var payload = $"{{\"tenantId\":\"t1\",\"incidentId\":\"{incidentId}\",\"edgeEventId\":\"edge-n1\"}}";
        var outbox = new FakeOutboxStore([new OutboxClaim(Guid.NewGuid(), "t1", "incident.created", payload, 0)]);
        var notifications = new FakeNotificationStore();
        var email = new FakeEmailSender();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOutboxDispatcherStore>(outbox);
        services.AddSingleton<INotificationStore>(notifications);
        services.AddSingleton<IEmailSender>(email);
        services.AddSingleton(new OutboxOptions());
        services.AddSingleton(new NotificationOptions { To = ["pilot@school"] });
        services.AddSingleton<OutboxDispatcher>();
        var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<OutboxDispatcher>().DispatchOnceAsync(CancellationToken.None);

        Assert.Single(email.Sent);
        var row = Assert.Single(notifications.Attempts);
        Assert.Equal("accepted", row.State);
        Assert.Equal("email", row.Channel);
        Assert.Equal(0, row.Level);
        Assert.Equal("pilot@school", row.Recipient);
        Assert.Equal(incidentId, row.IncidentId);
    }
}
