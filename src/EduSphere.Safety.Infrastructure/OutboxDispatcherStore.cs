using Dapper;
using EduSphere.Safety.Application;
using Npgsql;

namespace EduSphere.Safety.Infrastructure;

public sealed class OutboxDispatcherStore(PostgresOptions options) : IOutboxDispatcherStore
{
    private readonly PostgresOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task<IReadOnlyList<OutboxClaim>> ClaimDueAsync(int batchSize, DateTimeOffset leaseUntilUtc, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(ct);
        // Atomic lease: locked_until bump + attempts+1 in the same statement;
        // SKIP LOCKED lets multiple dispatchers share the queue.
        const string sql =
            "UPDATE outbox SET locked_until = @lease, attempts = attempts + 1 " +
            "WHERE id IN (SELECT id FROM outbox WHERE locked_until IS NULL OR locked_until < now() " +
            "ORDER BY created_at LIMIT @batch FOR UPDATE SKIP LOCKED) " +
            "RETURNING id AS \"Id\", tenant_id AS \"TenantId\", type AS \"Type\", payload::text AS \"Payload\", attempts AS \"Attempts\"";
        var rows = await connection.QueryAsync<OutboxClaim>(
            new CommandDefinition(sql, new { lease = leaseUntilUtc.UtcDateTime, batch = batchSize }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task DeleteAsync(Guid outboxId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition("DELETE FROM outbox WHERE id = @id", new { id = outboxId }, cancellationToken: ct));
    }

    public async Task RescheduleAsync(Guid outboxId, DateTimeOffset lockedUntilUtc, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE outbox SET locked_until = @locked WHERE id = @id",
                new { locked = lockedUntilUtc.UtcDateTime, id = outboxId },
                cancellationToken: ct));
    }

    private string RequireConnectionString() =>
        string.IsNullOrWhiteSpace(_options.ConnectionString)
            ? throw new InvalidOperationException("PostgresOptions.ConnectionString is not configured.")
            : _options.ConnectionString;
}
