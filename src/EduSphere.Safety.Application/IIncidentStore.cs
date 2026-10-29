using EduSphere.Safety.Core;
using EduSphere.Safety.Shared;

namespace EduSphere.Safety.Application;

public sealed record OutboxEntry(string TenantId, string Type, string Payload);

public sealed record ActivityEntry(string ActorId, string? Reason, DateTimeOffset OccurredAtUtc);

public interface IIncidentStore
{
    Task<Incident?> FindByEdgeEventAsync(string tenantId, string edgeEventId, CancellationToken ct);

    Task<Incident?> GetAsync(string tenantId, Guid incidentId, CancellationToken ct);

    // Tenant-scoped paged list. Simple limit/offset for Plan 1; cursor
    // pagination is a later plan.
    Task<IReadOnlyList<Incident>> ListAsync(string tenantId, int limit, int offset, CancellationToken ct);

    // Tenant-scoped dead-letter view: outbox rows with attempts >= 10.
    Task<IReadOnlyList<DeadLetterRow>> ListDeadLetterAsync(string tenantId, int limit, CancellationToken ct);

    /// <summary>
    /// Inserts the incident + outbox row in one unit of work and returns the
    /// effective incident id. Concurrent duplicate ingests of the same
    /// <c>(tenant_id, edge_event_id)</c> must never fail: on unique-violation
    /// (SQLSTATE 23505) the implementation writes NO second outbox row and
    /// returns the existing incident's id instead.
    /// </summary>
    Task<Guid> AddAsync(Incident incident, OutboxEntry outboxEntry, CancellationToken ct);

    /// <summary>
    /// Persists the already-mutated incident + activity + outbox in one unit of work
    /// ONLY when the stored version still equals expectedVersion; returns false on
    /// mismatch (lost race). Caller guarantees tenant scope via the loaded entity.
    /// Atomicity contract (Task 4): single-statement conditional
    /// <c>UPDATE ... SET state, version WHERE tenant_id AND id AND version = expected</c>
    /// (0 rows → false; write the supplied already-incremented version, never increment
    /// unconditionally; keep <c>tenant_id</c> in the WHERE even though the caller loaded
    /// the entity); activity + outbox inserts happen in the SAME transaction and ONLY
    /// when the predicate matched.
    /// </summary>
    Task<bool> SaveAsync(Incident incident, int expectedVersion, ActivityEntry activityEntry, OutboxEntry outboxEntry, CancellationToken ct);
}
