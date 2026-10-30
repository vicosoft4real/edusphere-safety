using Dapper;
using EduSphere.Safety.Application;
using Npgsql;

namespace EduSphere.Safety.Infrastructure;

public sealed class NotificationStore(PostgresOptions options) : INotificationStore
{
    private readonly PostgresOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task ScheduleFirstAsync(string tenantId, Guid incidentId, int level, DateTimeOffset dueAtUtc, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(ConnectionString());
        await connection.OpenAsync(ct);
        // Idempotent by primary key: concurrent duplicate incident creates
        // converge on one row (identical due_at, derived from the edge event).
        await connection.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO escalation_schedules (tenant_id, incident_id, level, due_at) VALUES (@tenant, @incident, @level, @due) ON CONFLICT (tenant_id, incident_id, level) DO NOTHING",
                new
                {
                    tenant = tenantId,
                    incident = incidentId,
                    level,
                    due = dueAtUtc.UtcDateTime,
                },
                cancellationToken: ct));
    }

    public async Task<IReadOnlyList<EscalationClaim>> ClaimDueAsync(int batchSize, DateTimeOffset leaseUntilUtc, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(ConnectionString());
        await connection.OpenAsync(ct);
        // Atomic lease like the outbox: bump + attempts in one statement;
        // SKIP LOCKED lets multiple dispatchers share the queue.
        const string sql =
            "UPDATE escalation_schedules SET locked_until = @lease, attempts = attempts + 1 " +
            "WHERE (tenant_id, incident_id, level) IN (SELECT tenant_id, incident_id, level FROM escalation_schedules WHERE due_at <= now() AND (locked_until IS NULL OR locked_until < now()) " +
            "ORDER BY due_at LIMIT @batch FOR UPDATE SKIP LOCKED) " +
            "RETURNING tenant_id AS \"TenantId\", incident_id AS \"IncidentId\", level AS \"Level\", due_at AS \"DueAtUtc\", attempts AS \"Attempts\"";
        var rows = await connection.QueryAsync<ClaimRow>(
            new CommandDefinition(sql, new { lease = leaseUntilUtc.UtcDateTime, batch = Math.Max(batchSize, 1) }, cancellationToken: ct));
        return rows.Select(r => new EscalationClaim(
            r.TenantId, r.IncidentId, r.Level, EnsureUtc(r.DueAtUtc), r.Attempts)).ToList();
    }

    public async Task<FireDecision> PrepareFireAsync(string tenantId, Guid incidentId, int level, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(ConnectionString());
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        // Recheck + cancel in ONE transaction: the state read locks the
        // incident row, so an acknowledgement racing the dispatcher either
        // lands before this read (schedule deleted, no send) or after the
        // send (at-least-once duplicate, accepted and documented).
        var state = await connection.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(
                "SELECT state FROM incidents WHERE tenant_id = @tenant AND id = @incident FOR UPDATE",
                new { tenant = tenantId, incident = incidentId },
                transaction: tx,
                cancellationToken: ct));
        if (!string.Equals(state, "New", StringComparison.Ordinal))
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    "DELETE FROM escalation_schedules WHERE tenant_id = @tenant AND incident_id = @incident AND level = @level",
                    new { tenant = tenantId, incident = incidentId, level },
                    transaction: tx,
                    cancellationToken: ct));
            await tx.CommitAsync(ct);
            return FireDecision.Cancelled;
        }

        await tx.CommitAsync(ct);
        return FireDecision.Proceed;
    }

    public async Task SettleFireAsync(
        string tenantId,
        Guid incidentId,
        int level,
        IReadOnlyList<DeliveryAttempt> attempts,
        int? nextLevel,
        DateTimeOffset? nextDueAtUtc,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        await using var connection = new NpgsqlConnection(ConnectionString());
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        foreach (var attempt in attempts)
            await InsertAttemptOnceAsync(connection, tx, attempt, ct);
        await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM escalation_schedules WHERE tenant_id = @tenant AND incident_id = @incident AND level = @level",
                new { tenant = tenantId, incident = incidentId, level },
                transaction: tx,
                cancellationToken: ct));
        if (nextLevel.HasValue && nextDueAtUtc.HasValue)
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    "INSERT INTO escalation_schedules (tenant_id, incident_id, level, due_at) VALUES (@tenant, @incident, @level, @due) ON CONFLICT (tenant_id, incident_id, level) DO NOTHING",
                    new
                    {
                        tenant = tenantId,
                        incident = incidentId,
                        level = nextLevel.Value,
                        due = nextDueAtUtc.Value.UtcDateTime,
                    },
                    transaction: tx,
                    cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
    }

    public async Task DeferFireAsync(
        string tenantId,
        Guid incidentId,
        int level,
        DateTimeOffset lockedUntilUtc,
        IReadOnlyList<DeliveryAttempt> attempts,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        await using var connection = new NpgsqlConnection(ConnectionString());
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        foreach (var attempt in attempts)
            await InsertAttemptOnceAsync(connection, tx, attempt, ct);
        await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE escalation_schedules SET locked_until = @locked WHERE tenant_id = @tenant AND incident_id = @incident AND level = @level",
                new
                {
                    locked = lockedUntilUtc.UtcDateTime,
                    tenant = tenantId,
                    incident = incidentId,
                    level,
                },
                transaction: tx,
                cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task CancelScheduleAsync(string tenantId, Guid incidentId, int level, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(ConnectionString());
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM escalation_schedules WHERE tenant_id = @tenant AND incident_id = @incident AND level = @level",
                new { tenant = tenantId, incident = incidentId, level },
                cancellationToken: ct));
    }

    public async Task RecordAttemptsAsync(IReadOnlyList<DeliveryAttempt> attempts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        if (attempts.Count == 0)
            return;
        await using var connection = new NpgsqlConnection(ConnectionString());
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        foreach (var attempt in attempts)
            await InsertAttemptOnceAsync(connection, tx, attempt, ct);
        await tx.CommitAsync(ct);
    }

    private static async Task InsertAttemptOnceAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, DeliveryAttempt attempt, CancellationToken ct)
    {
        // Lease-serialized writers + crash redelivery: skip recipients already
        // recorded accepted/delivered instead of relying on partial-index
        // conflict inference.
        var exists = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT 1 FROM delivery_attempts WHERE tenant_id = @tenant AND incident_id = @incident AND channel = @channel AND level = @level AND recipient = @recipient AND state IN ('accepted','delivered')",
                new
                {
                    tenant = attempt.TenantId,
                    incident = attempt.IncidentId,
                    channel = attempt.Channel,
                    level = attempt.Level,
                    recipient = attempt.Recipient,
                },
                transaction: tx,
                cancellationToken: ct));
        if (exists == 1)
            return;
        await connection.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO delivery_attempts (tenant_id, incident_id, channel, level, recipient, state, detail) VALUES (@tenant, @incident, @channel, @level, @recipient, @state, @detail)",
                new
                {
                    tenant = attempt.TenantId,
                    incident = attempt.IncidentId,
                    channel = attempt.Channel,
                    level = attempt.Level,
                    recipient = attempt.Recipient,
                    state = attempt.State,
                    detail = attempt.Detail,
                },
                transaction: tx,
                cancellationToken: ct));
    }

    private string ConnectionString() =>
        string.IsNullOrWhiteSpace(_options.ConnectionString)
            ? throw new InvalidOperationException("PostgresOptions.ConnectionString is not configured.")
            : _options.ConnectionString;

    private static DateTimeOffset EnsureUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed class ClaimRow
    {
        public string TenantId { get; set; } = string.Empty;
        public Guid IncidentId { get; set; }
        public int Level { get; set; }
        public DateTime DueAtUtc { get; set; }
        public int Attempts { get; set; }
    }
}
