using System.Text.Json;
using EduSphere.Safety.Application;
using EduSphere.Safety.Core;
using EduSphere.Safety.Shared;
using Xunit;

public sealed class IncidentTests
{
    [Fact]
    public void New_incident_can_be_acknowledged()
    {
        var incident = Incident.Create("t1", "campus-a", "ZoneIntrusion", "cam-1");
        var result = incident.TransitionTo(IncidentState.Acknowledged, "staff-1");
        Assert.True(result.IsSuccess);
        Assert.Equal(IncidentState.Acknowledged, incident.State);
    }

    [Fact]
    public void New_incident_cannot_skip_to_resolved()
    {
        var incident = Incident.Create("t1", "campus-a", "ZoneIntrusion", "cam-1");
        var result = incident.TransitionTo(IncidentState.Resolved, "staff-1");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Dismiss_requires_reason()
    {
        var incident = Incident.Create("t1", "campus-a", "ZoneIntrusion", "cam-1");
        incident.TransitionTo(IncidentState.Acknowledged, "staff-1");
        incident.TransitionTo(IncidentState.Investigating, "staff-1");
        Assert.False(incident.TransitionTo(IncidentState.Dismissed, "staff-1", reason: null).IsSuccess);
        Assert.True(incident.TransitionTo(IncidentState.Dismissed, "staff-1", reason: "false alarm").IsSuccess);
    }
}

public sealed class IncidentTransitionTests
{
    [Fact]
    public void Resolved_is_terminal()
    {
        var incident = Incident.Create("t1", "campus-a", "ZoneIntrusion", "cam-1");
        Assert.True(incident.TransitionTo(IncidentState.Acknowledged, "staff-1").IsSuccess);
        Assert.True(incident.TransitionTo(IncidentState.Investigating, "staff-1").IsSuccess);
        Assert.True(incident.TransitionTo(IncidentState.Resolved, "staff-1").IsSuccess);
        var result = incident.TransitionTo(IncidentState.Investigating, "staff-1");
        Assert.False(result.IsSuccess);
        Assert.Equal("incident.invalid_transition", result.Error?.Code);
    }

    [Fact]
    public void Dismissed_is_terminal()
    {
        var incident = Incident.Create("t1", "campus-a", "ZoneIntrusion", "cam-1");
        Assert.True(incident.TransitionTo(IncidentState.Acknowledged, "staff-1").IsSuccess);
        Assert.True(incident.TransitionTo(IncidentState.Dismissed, "staff-1", reason: "false alarm").IsSuccess);
        var result = incident.TransitionTo(IncidentState.Acknowledged, "staff-1");
        Assert.False(result.IsSuccess);
        Assert.Equal("incident.invalid_transition", result.Error?.Code);
    }

    [Fact]
    public void Acknowledged_cannot_skip_to_resolved()
    {
        var incident = Incident.Create("t1", "campus-a", "ZoneIntrusion", "cam-1");
        Assert.True(incident.TransitionTo(IncidentState.Acknowledged, "staff-1").IsSuccess);
        var result = incident.TransitionTo(IncidentState.Resolved, "staff-1");
        Assert.False(result.IsSuccess);
        Assert.Equal("incident.invalid_transition", result.Error?.Code);
    }

    [Fact]
    public void Dismiss_with_whitespace_reason_fails()
    {
        var incident = Incident.Create("t1", "campus-a", "ZoneIntrusion", "cam-1");
        Assert.True(incident.TransitionTo(IncidentState.Acknowledged, "staff-1").IsSuccess);
        Assert.True(incident.TransitionTo(IncidentState.Investigating, "staff-1").IsSuccess);
        var blank = incident.TransitionTo(IncidentState.Dismissed, "staff-1", reason: "   ");
        Assert.False(blank.IsSuccess);
        Assert.Equal("incident.dismiss_reason_required", blank.Error?.Code);
        var missing = incident.TransitionTo(IncidentState.Dismissed, "staff-1", reason: null);
        Assert.False(missing.IsSuccess);
        Assert.Equal("incident.dismiss_reason_required", missing.Error?.Code);
    }

    [Fact]
    public void Version_increments_on_each_transition()
    {
        var incident = Incident.Create("t1", "campus-a", "ZoneIntrusion", "cam-1");
        Assert.Equal(0, incident.Version);
        Assert.True(incident.TransitionTo(IncidentState.Acknowledged, "staff-1").IsSuccess);
        Assert.Equal(1, incident.Version);
        Assert.True(incident.TransitionTo(IncidentState.Investigating, "staff-1").IsSuccess);
        Assert.Equal(2, incident.Version);
    }
}

public sealed class IncidentHandlerTests
{
    internal sealed class FakeIncidentStore : IIncidentStore
    {
        // Immutable snapshots, like rows a real repo would read. Every read materializes
        // a FRESH Incident via the domain's own Rehydrate factory, so each handler owns
        // an independent single-owner-per-request copy and can never alias stored state.
        private sealed record Snapshot(
            Guid Id,
            string TenantId,
            string CampusId,
            string EventType,
            string CameraId,
            string? EdgeEventId,
            IncidentState State,
            int Version);

        private readonly Dictionary<(string TenantId, Guid IncidentId), Snapshot> _snapshots = new();
        public List<OutboxEntry> Outbox { get; } = [];
        public List<(Guid IncidentId, ActivityEntry Activity)> Activities { get; } = [];
        public List<DeadLetterRow> DeadLetters { get; } = [];

        /// <summary>
        /// When true, the next SaveAsync call returns false without persisting,
        /// simulating a lost optimistic-concurrency race.
        /// </summary>
        public bool FailNextSave { get; set; }

        private static Snapshot Take(Incident incident) => new(
            incident.Id,
            incident.TenantId,
            incident.CampusId,
            incident.EventType,
            incident.CameraId,
            incident.EdgeEventId,
            incident.State,
            incident.Version);

        private static Incident Materialize(Snapshot snapshot) => Incident.Rehydrate(
            snapshot.Id,
            snapshot.TenantId,
            snapshot.CampusId,
            snapshot.EventType,
            snapshot.CameraId,
            snapshot.EdgeEventId,
            snapshot.State,
            snapshot.Version);

        public Task<Incident?> FindByEdgeEventAsync(string tenantId, string edgeEventId, CancellationToken ct)
        {
            var snapshot = _snapshots.Values.FirstOrDefault(s => s.TenantId == tenantId && s.EdgeEventId == edgeEventId);
            return Task.FromResult(snapshot is null ? null : Materialize(snapshot));
        }

        public Task<Incident?> GetAsync(string tenantId, Guid incidentId, CancellationToken ct)
            => Task.FromResult(_snapshots.TryGetValue((tenantId, incidentId), out var snapshot) ? Materialize(snapshot) : null);

        public Task<IReadOnlyList<Incident>> ListAsync(string tenantId, int limit, int offset, CancellationToken ct)
        {
            IReadOnlyList<Incident> page = _snapshots.Values
                .Where(s => s.TenantId == tenantId)
                .OrderBy(s => s.Id)
                .Skip(Math.Max(offset, 0))
                .Take(Math.Clamp(limit, 1, 200))
                .Select(Materialize)
                .ToList();
            return Task.FromResult(page);
        }

        public Task<IReadOnlyList<DeadLetterRow>> ListDeadLetterAsync(string tenantId, int limit, CancellationToken ct)
        {
            IReadOnlyList<DeadLetterRow> rows = DeadLetters
                .Where(d => d.TenantId == tenantId && d.Attempts >= 10)
                .Take(Math.Max(limit, 0))
                .ToList();
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

        public Task<bool> SaveAsync(
            Incident incident,
            int expectedVersion,
            ActivityEntry activityEntry,
            OutboxEntry outboxEntry,
            CancellationToken ct)
        {
            var key = (incident.TenantId, incident.Id);
            if (FailNextSave)
            {
                FailNextSave = false;
                return Task.FromResult(false);
            }
            if (!_snapshots.TryGetValue(key, out var stored) || stored.Version != expectedVersion)
                return Task.FromResult(false);
            _snapshots[key] = Take(incident);
            Activities.Add((incident.Id, activityEntry));
            Outbox.Add(outboxEntry);
            return Task.FromResult(true);
        }
    }

    private static CreateIncidentCommand ValidCreate(string edgeEventId = "edge-1") => new(
        EdgeEventId: edgeEventId,
        TenantId: "t1",
        CampusId: "campus-a",
        EventType: "ZoneIntrusion",
        CameraId: "cam-1",
        DetectedAtUtc: DateTimeOffset.UtcNow,
        RuleVersion: "rules-3",
        ModelVersion: "model-7");

    private static TransitionIncidentCommand TransitionTo(Guid incidentId, string tenantId, int expectedVersion, IncidentState next, string? reason = null) => new(
        IncidentId: incidentId,
        TenantId: tenantId,
        ExpectedVersion: expectedVersion,
        Next: next,
        ActorId: "staff-1",
        Reason: reason);

    [Fact]
    public async Task Duplicate_create_returns_same_id()
    {
        var store = new FakeIncidentStore();
        var handler = new CreateIncidentHandler(store, new Plan3NotificationTests.FakeNotificationStore(), Plan3NotificationTests.EmptyPolicyStore());
        var first = await handler.HandleAsync(ValidCreate(), CancellationToken.None);
        var second = await handler.HandleAsync(ValidCreate(), CancellationToken.None);
        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(first.Value, second.Value);
        Assert.NotEqual(Guid.Empty, first.Value);
        Assert.Single(store.Outbox);
    }

    [Fact]
    public async Task Store_double_add_same_edge_returns_same_id_with_single_outbox_row()
    {
        var store = new FakeIncidentStore();
        var first = Incident.Create("t1", "campus-a", "ZoneIntrusion", "cam-1", "edge-race-1");
        var rival = Incident.Create("t1", "campus-a", "ZoneIntrusion", "cam-1", "edge-race-1");
        Assert.NotEqual(first.Id, rival.Id);

        var firstId = await store.AddAsync(first, new OutboxEntry("t1", "incident.created", "{}"), CancellationToken.None);
        var secondId = await store.AddAsync(rival, new OutboxEntry("t1", "incident.created", "{}"), CancellationToken.None);

        Assert.Equal(first.Id, firstId);
        Assert.Equal(firstId, secondId);
        Assert.Single(store.Outbox);
    }

    [Fact]
    public async Task Store_conflict_path_returns_existing_id_without_second_outbox_row()
    {
        var store = new FakeIncidentStore();
        var handler = new CreateIncidentHandler(store, new Plan3NotificationTests.FakeNotificationStore(), Plan3NotificationTests.EmptyPolicyStore());
        var created = await handler.HandleAsync(ValidCreate("edge-conflict-1"), CancellationToken.None);
        Assert.True(created.IsSuccess);

        // A concurrent duplicate that slipped past the handler's pre-check:
        // AddAsync must resolve it to the winner's id with no extra outbox row.
        var rival = Incident.Create("t1", "campus-a", "ZoneIntrusion", "cam-1", "edge-conflict-1");
        var effectiveId = await store.AddAsync(rival, new OutboxEntry("t1", "incident.created", "{}"), CancellationToken.None);

        Assert.Equal(created.Value, effectiveId);
        Assert.Single(store.Outbox);
    }

    [Fact]
    public async Task Duplicate_create_is_scoped_by_tenant()
    {
        var store = new FakeIncidentStore();
        var handler = new CreateIncidentHandler(store, new Plan3NotificationTests.FakeNotificationStore(), Plan3NotificationTests.EmptyPolicyStore());
        var first = await handler.HandleAsync(ValidCreate(), CancellationToken.None);
        var otherTenant = await handler.HandleAsync(ValidCreate() with { TenantId = "t2" }, CancellationToken.None);
        Assert.True(first.IsSuccess);
        Assert.True(otherTenant.IsSuccess);
        Assert.NotEqual(first.Value, otherTenant.Value);
    }

    [Fact]
    public async Task Create_rejects_missing_fields()
    {
        var handler = new CreateIncidentHandler(new IncidentHandlerTests.FakeIncidentStore(), new Plan3NotificationTests.FakeNotificationStore(), Plan3NotificationTests.EmptyPolicyStore());
        var result = await handler.HandleAsync(ValidCreate() with { TenantId = "" }, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("validation.failed", result.Error?.Code);
    }

    [Fact]
    public async Task Create_rejects_detectedAt_more_than_5min_in_future()
    {
        var handler = new CreateIncidentHandler(new IncidentHandlerTests.FakeIncidentStore(), new Plan3NotificationTests.FakeNotificationStore(), Plan3NotificationTests.EmptyPolicyStore());
        var result = await handler.HandleAsync(
            ValidCreate() with { DetectedAtUtc = DateTimeOffset.UtcNow.AddMinutes(30) },
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("validation.failed", result.Error?.Code);
    }

    [Fact]
    public async Task Transition_acknowledges_with_current_version()
    {
        var store = new FakeIncidentStore();
        var created = await new CreateIncidentHandler(store, new Plan3NotificationTests.FakeNotificationStore(), Plan3NotificationTests.EmptyPolicyStore()).HandleAsync(ValidCreate(), CancellationToken.None);
        Assert.True(created.IsSuccess);
        var incidentId = created.Value;
        Assert.NotEqual(Guid.Empty, incidentId);

        var result = await new TransitionIncidentHandler(store).HandleAsync(
            TransitionTo(incidentId, "t1", 0, IncidentState.Acknowledged), CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal(incidentId, result.Value);

        var loaded = await store.GetAsync("t1", incidentId, CancellationToken.None);
        Assert.Equal(IncidentState.Acknowledged, loaded?.State);
        Assert.Equal(1, loaded?.Version ?? -1);
        Assert.Single(store.Activities);
    }

    [Fact]
    public async Task Stale_version_returns_concurrency_conflict()
    {
        var store = new FakeIncidentStore();
        var created = await new CreateIncidentHandler(store, new Plan3NotificationTests.FakeNotificationStore(), Plan3NotificationTests.EmptyPolicyStore()).HandleAsync(ValidCreate(), CancellationToken.None);
        var incidentId = created.Value;
        var handler = new TransitionIncidentHandler(store);
        var first = await handler.HandleAsync(
            TransitionTo(incidentId, "t1", 0, IncidentState.Acknowledged), CancellationToken.None);
        Assert.True(first.IsSuccess);
        var stale = await handler.HandleAsync(
            TransitionTo(incidentId, "t1", 0, IncidentState.Investigating), CancellationToken.None);
        Assert.False(stale.IsSuccess);
        Assert.Equal("incident.concurrency_conflict", stale.Error?.Code);
    }

    [Fact]
    public async Task Transition_unknown_incident_returns_not_found()
    {
        var handler = new TransitionIncidentHandler(new FakeIncidentStore());
        var result = await handler.HandleAsync(
            TransitionTo(Guid.NewGuid(), "t1", 0, IncidentState.Acknowledged), CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("incident.not_found", result.Error?.Code);
    }

    [Fact]
    public async Task Transition_cross_tenant_returns_not_found()
    {
        var store = new FakeIncidentStore();
        var created = await new CreateIncidentHandler(store, new Plan3NotificationTests.FakeNotificationStore(), Plan3NotificationTests.EmptyPolicyStore()).HandleAsync(ValidCreate(), CancellationToken.None);
        var incidentId = created.Value;
        var result = await new TransitionIncidentHandler(store).HandleAsync(
            TransitionTo(incidentId, "t2", 0, IncidentState.Acknowledged), CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("incident.not_found", result.Error?.Code);
    }

    [Fact]
    public async Task Transition_missing_incident_returns_not_found()
    {
        var handler = new TransitionIncidentHandler(new FakeIncidentStore());
        var result = await handler.HandleAsync(
            TransitionTo(Guid.NewGuid(), "t1", 0, IncidentState.Investigating), CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("incident.not_found", result.Error?.Code);
    }

    [Fact]
    public async Task Transition_acknowledged_to_resolved_returns_invalid_transition_not_conflict()
    {
        var store = new FakeIncidentStore();
        var created = await new CreateIncidentHandler(store, new Plan3NotificationTests.FakeNotificationStore(), Plan3NotificationTests.EmptyPolicyStore()).HandleAsync(ValidCreate(), CancellationToken.None);
        var incidentId = created.Value;
        var handler = new TransitionIncidentHandler(store);
        var acked = await handler.HandleAsync(
            TransitionTo(incidentId, "t1", 0, IncidentState.Acknowledged), CancellationToken.None);
        Assert.True(acked.IsSuccess);
        var illegal = await handler.HandleAsync(
            TransitionTo(incidentId, "t1", 1, IncidentState.Resolved), CancellationToken.None);
        Assert.False(illegal.IsSuccess);
        Assert.Equal("incident.invalid_transition", illegal.Error?.Code);
        Assert.NotEqual("incident.concurrency_conflict", illegal.Error?.Code);
    }

    [Fact]
    public async Task Transition_lost_race_on_save_returns_concurrency_conflict()
    {
        var store = new FakeIncidentStore();
        var created = await new CreateIncidentHandler(store, new Plan3NotificationTests.FakeNotificationStore(), Plan3NotificationTests.EmptyPolicyStore()).HandleAsync(ValidCreate(), CancellationToken.None);
        var incidentId = created.Value;
        store.FailNextSave = true;
        var result = await new TransitionIncidentHandler(store).HandleAsync(
            TransitionTo(incidentId, "t1", 0, IncidentState.Acknowledged), CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("incident.concurrency_conflict", result.Error?.Code);
    }

    [Fact]
    public async Task Failed_save_leaves_stored_state_and_outbox_unchanged()
    {
        var store = new FakeIncidentStore();
        var created = await new CreateIncidentHandler(store, new Plan3NotificationTests.FakeNotificationStore(), Plan3NotificationTests.EmptyPolicyStore()).HandleAsync(ValidCreate(), CancellationToken.None);
        var incidentId = created.Value;
        Assert.Single(store.Outbox);

        store.FailNextSave = true;
        var result = await new TransitionIncidentHandler(store).HandleAsync(
            TransitionTo(incidentId, "t1", 0, IncidentState.Acknowledged), CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("incident.concurrency_conflict", result.Error?.Code);

        var stored = await store.GetAsync("t1", incidentId, CancellationToken.None);
        Assert.Equal(IncidentState.New, stored?.State);
        Assert.Equal(0, stored?.Version);
        Assert.Single(store.Outbox);
        Assert.Empty(store.Activities);
    }

    [Fact]
    public async Task Concurrent_views_are_independent_objects()
    {
        var store = new FakeIncidentStore();
        var created = await new CreateIncidentHandler(store, new Plan3NotificationTests.FakeNotificationStore(), Plan3NotificationTests.EmptyPolicyStore()).HandleAsync(ValidCreate(), CancellationToken.None);
        var incidentId = created.Value;
        var handler = new TransitionIncidentHandler(store);
        Assert.True((await handler.HandleAsync(
            TransitionTo(incidentId, "t1", 0, IncidentState.Acknowledged), CancellationToken.None)).IsSuccess);

        var viewA = await store.GetAsync("t1", incidentId, CancellationToken.None);
        var viewB = await store.GetAsync("t1", incidentId, CancellationToken.None);
        Assert.NotNull(viewA);
        Assert.NotNull(viewB);
        Assert.NotSame(viewA, viewB);

        Assert.True(viewA.TransitionTo(IncidentState.Investigating, "staff-1").IsSuccess);
        Assert.Equal(IncidentState.Investigating, viewA.State);
        Assert.Equal(IncidentState.Acknowledged, viewB.State);

        var stored = await store.GetAsync("t1", incidentId, CancellationToken.None);
        Assert.Equal(IncidentState.Acknowledged, stored?.State);
        Assert.Equal(1, stored?.Version);
    }

    [Fact]
    public async Task Stale_version_with_illegal_move_returns_concurrency_conflict()
    {
        var store = new FakeIncidentStore();
        var created = await new CreateIncidentHandler(store, new Plan3NotificationTests.FakeNotificationStore(), Plan3NotificationTests.EmptyPolicyStore()).HandleAsync(ValidCreate(), CancellationToken.None);
        var incidentId = created.Value;
        var handler = new TransitionIncidentHandler(store);
        Assert.True((await handler.HandleAsync(
            TransitionTo(incidentId, "t1", 0, IncidentState.Acknowledged), CancellationToken.None)).IsSuccess);

        // Stored version is now 1; expectedVersion 0 is stale AND Acknowledged→Resolved
        // is an illegal move. Version check must run first → conflict, not 422.
        var result = await handler.HandleAsync(
            TransitionTo(incidentId, "t1", 0, IncidentState.Resolved), CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("incident.concurrency_conflict", result.Error?.Code);
    }

    [Fact]
    public async Task Updated_outbox_payload_carries_post_increment_version()
    {
        var store = new FakeIncidentStore();
        var created = await new CreateIncidentHandler(store, new Plan3NotificationTests.FakeNotificationStore(), Plan3NotificationTests.EmptyPolicyStore()).HandleAsync(ValidCreate(), CancellationToken.None);
        var incidentId = created.Value;
        Assert.True((await new TransitionIncidentHandler(store).HandleAsync(
            TransitionTo(incidentId, "t1", 0, IncidentState.Acknowledged), CancellationToken.None)).IsSuccess);

        Assert.Equal(2, store.Outbox.Count);
        var payload = JsonDocument.Parse(store.Outbox.Last().Payload).RootElement;
        Assert.Equal("incident.updated", store.Outbox.Last().Type);
        Assert.Equal(1, payload.GetProperty("version").GetInt32());
        Assert.Equal(0, payload.GetProperty("expectedVersion").GetInt32());
    }

    [Fact]
    public async Task Transition_cross_tenant_lookup_null_returns_not_found()
    {
        var store = new FakeIncidentStore();
        var created = await new CreateIncidentHandler(store, new Plan3NotificationTests.FakeNotificationStore(), Plan3NotificationTests.EmptyPolicyStore()).HandleAsync(ValidCreate(), CancellationToken.None);
        var incidentId = created.Value;
        var crossTenantLoad = await store.GetAsync("t2", incidentId, CancellationToken.None);
        Assert.Null(crossTenantLoad);
        var result = await new TransitionIncidentHandler(store).HandleAsync(
            TransitionTo(incidentId, "t2", 0, IncidentState.Investigating), CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("incident.not_found", result.Error?.Code);
    }

    [Fact]
    public async Task Transition_invalid_move_returns_invalid_transition_code()
    {
        var store = new FakeIncidentStore();
        var created = await new CreateIncidentHandler(store, new Plan3NotificationTests.FakeNotificationStore(), Plan3NotificationTests.EmptyPolicyStore()).HandleAsync(ValidCreate(), CancellationToken.None);
        var incidentId = created.Value;
        var handler = new TransitionIncidentHandler(store);
        var acked = await handler.HandleAsync(
            TransitionTo(incidentId, "t1", 0, IncidentState.Acknowledged), CancellationToken.None);
        Assert.True(acked.IsSuccess);
        var skipped = await handler.HandleAsync(
            TransitionTo(incidentId, "t1", 1, IncidentState.Resolved), CancellationToken.None);
        Assert.False(skipped.IsSuccess);
        Assert.Equal("incident.invalid_transition", skipped.Error?.Code);
    }

    [Fact]
    public async Task Transition_dismiss_with_whitespace_reason_fails()
    {
        var store = new FakeIncidentStore();
        var created = await new CreateIncidentHandler(store, new Plan3NotificationTests.FakeNotificationStore(), Plan3NotificationTests.EmptyPolicyStore()).HandleAsync(ValidCreate(), CancellationToken.None);
        var incidentId = created.Value;
        var handler = new TransitionIncidentHandler(store);
        Assert.True((await handler.HandleAsync(
            TransitionTo(incidentId, "t1", 0, IncidentState.Acknowledged), CancellationToken.None)).IsSuccess);
        Assert.True((await handler.HandleAsync(
            TransitionTo(incidentId, "t1", 1, IncidentState.Investigating), CancellationToken.None)).IsSuccess);
        var dismissed = await handler.HandleAsync(
            TransitionTo(incidentId, "t1", 2, IncidentState.Dismissed, reason: "   "), CancellationToken.None);
        Assert.False(dismissed.IsSuccess);
        Assert.Equal("incident.dismiss_reason_required", dismissed.Error?.Code);
    }

    [Fact]
    public async Task Transition_rejects_empty_actor()
    {
        var store = new FakeIncidentStore();
        var created = await new CreateIncidentHandler(store, new Plan3NotificationTests.FakeNotificationStore(), Plan3NotificationTests.EmptyPolicyStore()).HandleAsync(ValidCreate(), CancellationToken.None);
        var incidentId = created.Value;
        var command = TransitionTo(incidentId, "t1", 0, IncidentState.Acknowledged) with { ActorId = "" };
        var result = await new TransitionIncidentHandler(store).HandleAsync(command, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("validation.failed", result.Error?.Code);
    }
}

public sealed class SenderTests
{
    public sealed record PingCommand(string Text) : ICommand<string>;

    public sealed class PingHandler : ICommandHandler<PingCommand, string>
    {
        public Task<Result<string>> HandleAsync(PingCommand command, CancellationToken ct)
            => Task.FromResult(Result<string>.Ok("pong:" + command.Text));
    }

    private sealed class MapServiceProvider(Func<Type, object?> resolve) : IServiceProvider
    {
        public object? GetService(Type serviceType) => resolve(serviceType);
    }

    [Fact]
    public async Task SendAsync_dispatches_to_registered_handler()
    {
        var provider = new MapServiceProvider(t =>
            t == typeof(ICommandHandler<PingCommand, string>) ? new PingHandler() : null);
        var result = await new Sender(provider).SendAsync(new PingCommand("hello"), CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal("pong:hello", result.Value);
    }

    [Fact]
    public async Task SendAsync_missing_handler_throws()
    {
        var provider = new MapServiceProvider(_ => null);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new Sender(provider).SendAsync(new PingCommand("hello"), CancellationToken.None));
    }
}
