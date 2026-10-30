namespace EduSphere.Safety.Application;

/// <summary>
/// Outcome of the pre-send recheck: proceed with the send, or drop the
/// schedule silently (incident acknowledged, resolved, or gone).
/// </summary>
public enum FireDecision
{
    Proceed,
    Cancelled,
}

public sealed record EscalationClaim(
    string TenantId,
    Guid IncidentId,
    int Level,
    DateTimeOffset DueAtUtc,
    int Attempts);

public sealed record DeliveryAttempt(
    string TenantId,
    Guid IncidentId,
    string Channel,
    int Level,
    string Recipient,
    string State,
    string Detail);

/// <summary>
/// Escalation schedules + delivery-attempt ledger (migration 006).
/// ClaimDue serializes rows via lease (SKIP LOCKED, like the outbox);
/// claim bumps attempts. PrepareFire rechecks incident state and deletes the
/// schedule in the SAME transaction when there is nothing to send — this
/// closes the acknowledge-vs-escalation race on the cancel path. SettleFire
/// records attempts, deletes the fired row, and inserts the next level (or
/// nothing) atomically. All schedule inserts are idempotent: concurrent
/// duplicate incident creates converge on one row.
/// </summary>
public interface INotificationStore
{
    Task ScheduleFirstAsync(string tenantId, Guid incidentId, int level, DateTimeOffset dueAtUtc, CancellationToken ct);

    Task<IReadOnlyList<EscalationClaim>> ClaimDueAsync(int batchSize, DateTimeOffset leaseUntilUtc, CancellationToken ct);

    Task<FireDecision> PrepareFireAsync(string tenantId, Guid incidentId, int level, CancellationToken ct);

    Task SettleFireAsync(
        string tenantId,
        Guid incidentId,
        int level,
        IReadOnlyList<DeliveryAttempt> attempts,
        int? nextLevel,
        DateTimeOffset? nextDueAtUtc,
        CancellationToken ct);

    Task DeferFireAsync(
        string tenantId,
        Guid incidentId,
        int level,
        DateTimeOffset lockedUntilUtc,
        IReadOnlyList<DeliveryAttempt> attempts,
        CancellationToken ct);

    Task CancelScheduleAsync(string tenantId, Guid incidentId, int level, CancellationToken ct);

    /// <summary>
    /// Bare ledger append (level-0 outbox path). Skips recipients already
    /// recorded accepted/delivered, like the settled paths.
    /// </summary>
    Task RecordAttemptsAsync(IReadOnlyList<DeliveryAttempt> attempts, CancellationToken ct);
}
