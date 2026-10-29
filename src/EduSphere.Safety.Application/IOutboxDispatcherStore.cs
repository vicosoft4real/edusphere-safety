namespace EduSphere.Safety.Application;

public sealed record OutboxClaim(
    Guid Id,
    string TenantId,
    string Type,
    string Payload,
    int Attempts);

/// <summary>
/// Lease-based outbox access for the Worker dispatcher. Claim atomically
/// leases due rows (locked_until bump + attempts+1); completion is DELETE on
/// success, a future locked_until on retry, or a 24h park once attempts>=10
/// (parked rows stay visible to the dead-letter endpoint).
/// </summary>
public interface IOutboxDispatcherStore
{
    Task<IReadOnlyList<OutboxClaim>> ClaimDueAsync(int batchSize, DateTimeOffset leaseUntilUtc, CancellationToken ct);

    Task DeleteAsync(Guid outboxId, CancellationToken ct);

    Task RescheduleAsync(Guid outboxId, DateTimeOffset lockedUntilUtc, CancellationToken ct);
}
