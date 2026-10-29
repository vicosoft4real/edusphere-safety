using System.Text.Json;
using Dapper;
using EduSphere.Safety.Application;
using EduSphere.Safety.Core;
using Npgsql;

namespace EduSphere.Safety.Infrastructure;

// NOTE: no RLS policies exist in migration 001. Every transaction still sets
// `app.tenant` (via set_config ..., true, i.e. SET LOCAL) as a
// defense-in-depth hook for future policies; tenant isolation is enforced by
// the tenant_id predicate present in every query below.
public sealed class IncidentRepository(PostgresOptions options) : IIncidentStore
{
    private readonly PostgresOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task<Incident?> FindByEdgeEventAsync(string tenantId, string edgeEventId, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var transaction = await BeginTenantTransactionAsync(connection, tenantId, ct);
        var row = await connection.QuerySingleOrDefaultAsync<IncidentRow>(
            new CommandDefinition(FindByEdgeEventSql, new { tenant = tenantId, edge = edgeEventId }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return row is null ? null : Map(row);
    }

    public async Task<Incident?> GetAsync(string tenantId, Guid incidentId, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var transaction = await BeginTenantTransactionAsync(connection, tenantId, ct);
        var row = await connection.QuerySingleOrDefaultAsync<IncidentRow>(
            new CommandDefinition(GetSql, new { tenant = tenantId, id = incidentId }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<Incident>> ListAsync(string tenantId, int limit, int offset, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var transaction = await BeginTenantTransactionAsync(connection, tenantId, ct);
        var rows = await connection.QueryAsync<IncidentRow>(
            new CommandDefinition(ListSql, new { tenant = tenantId, limit, offset }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<DeadLetterRow>> ListDeadLetterAsync(string tenantId, int limit, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var transaction = await BeginTenantTransactionAsync(connection, tenantId, ct);
        var rows = await connection.QueryAsync<DeadLetterRowDto>(
            new CommandDefinition(DeadLetterSql, new { tenant = tenantId, limit }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return rows.Select(r => new DeadLetterRow(
            r.Id, r.TenantId, r.Type, r.Payload, r.Attempts, EnsureUtc(r.CreatedAt))).ToList();
    }

    public async Task<Guid> AddAsync(Incident incident, OutboxEntry outboxEntry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentNullException.ThrowIfNull(outboxEntry);
        await using var connection = OpenConnection();
        await using var transaction = await BeginTenantTransactionAsync(connection, incident.TenantId, ct);
        var (detectedAt, ruleVersion, modelVersion) = ExtractIngestMetadata(outboxEntry.Payload);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                InsertIncidentSql,
                new
                {
                    tenant = incident.TenantId,
                    id = incident.Id,
                    campus = incident.CampusId,
                    @event = incident.EventType,
                    camera = incident.CameraId,
                    edge = incident.EdgeEventId,
                    state = incident.State.ToString(),
                    version = incident.Version,
                    detectedAt,
                    ruleVersion,
                    modelVersion,
                },
                transaction,
                cancellationToken: ct));
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            // Lost a concurrent duplicate-ingest race: the failed statement
            // aborted this transaction, so roll back and return the winner's
            // id WITHOUT writing a second outbox row.
            await transaction.RollbackAsync(ct);
            if (incident.EdgeEventId is not null)
            {
                var existing = await FindByEdgeEventAsync(incident.TenantId, incident.EdgeEventId, ct);
                if (existing is not null)
                    return existing.Id;
            }

            throw;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            InsertOutboxSql,
            new { tenant = outboxEntry.TenantId, type = outboxEntry.Type, payload = outboxEntry.Payload },
            transaction,
            cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return incident.Id;
    }

    public async Task<bool> SaveAsync(
        Incident incident,
        int expectedVersion,
        ActivityEntry activityEntry,
        OutboxEntry outboxEntry,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentNullException.ThrowIfNull(activityEntry);
        ArgumentNullException.ThrowIfNull(outboxEntry);
        await using var connection = OpenConnection();
        await using var transaction = await BeginTenantTransactionAsync(connection, incident.TenantId, ct);
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            ConditionalUpdateSql,
            new
            {
                tenant = incident.TenantId,
                id = incident.Id,
                state = incident.State.ToString(),
                version = incident.Version,
                expected = expectedVersion,
            },
            transaction,
            cancellationToken: ct));
        if (rows != 1)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            InsertActivitySql,
            new
            {
                tenant = incident.TenantId,
                incidentId = incident.Id,
                actor = activityEntry.ActorId,
                reason = activityEntry.Reason,
                occurredAt = activityEntry.OccurredAtUtc,
            },
            transaction,
            cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            InsertOutboxSql,
            new { tenant = outboxEntry.TenantId, type = outboxEntry.Type, payload = outboxEntry.Payload },
            transaction,
            cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return true;
    }

    private NpgsqlConnection OpenConnection()
    {
        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
            throw new InvalidOperationException("PostgresOptions.ConnectionString is not configured.");
        return new NpgsqlConnection(_options.ConnectionString);
    }

    private static async Task<NpgsqlTransaction> BeginTenantTransactionAsync(
        NpgsqlConnection connection,
        string tenantId,
        CancellationToken ct)
    {
        await connection.OpenAsync(ct);
        var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT set_config('app.tenant', @tenant, true)",
            new { tenant = tenantId },
            transaction,
            cancellationToken: ct));
        return transaction;
    }

    private static Incident Map(IncidentRow row) => Incident.Rehydrate(
        row.Id,
        row.TenantId,
        row.CampusId,
        row.EventType,
        row.CameraId,
        row.EdgeEventId,
        Enum.Parse<IncidentState>(row.State, ignoreCase: false),
        row.Version);

    // The store interface carries no ingest-metadata fields, so detected_at /
    // rule_version / model_version are recovered from the incident.created
    // outbox payload written by the same unit of work; anything else (or an
    // unparsable payload) falls back to column defaults.
    private static (DateTimeOffset DetectedAt, string RuleVersion, string ModelVersion) ExtractIngestMetadata(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            var detectedAt = root.TryGetProperty("detectedAtUtc", out var detected)
                && detected.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(detected.GetString(), out var parsed)
                    ? parsed
                    : DateTimeOffset.UtcNow;
            return (detectedAt, StringProperty(root, "ruleVersion"), StringProperty(root, "modelVersion"));
        }
        catch (JsonException)
        {
            return (DateTimeOffset.UtcNow, string.Empty, string.Empty);
        }
    }

    private static string StringProperty(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;

    private sealed class IncidentRow
    {
        public Guid Id { get; set; }
        public string TenantId { get; set; } = string.Empty;
        public string CampusId { get; set; } = string.Empty;
        public string EventType { get; set; } = string.Empty;
        public string CameraId { get; set; } = string.Empty;
        public string? EdgeEventId { get; set; }
        public string State { get; set; } = string.Empty;
        public int Version { get; set; }
    }

    private sealed class DeadLetterRowDto
    {
        public Guid Id { get; set; }
        public string TenantId { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Payload { get; set; } = string.Empty;
        public int Attempts { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private static DateTimeOffset EnsureUtc(DateTime value) =>
        new(value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value);

    private const string IncidentColumns =
        "id AS \"Id\", tenant_id AS \"TenantId\", campus_id AS \"CampusId\", " +
        "event_type AS \"EventType\", camera_id AS \"CameraId\", edge_event_id AS \"EdgeEventId\", " +
        "state AS \"State\", version AS \"Version\"";

    private const string GetSql =
        "SELECT " + IncidentColumns + " FROM incidents WHERE tenant_id = @tenant AND id = @id";

    private const string FindByEdgeEventSql =
        "SELECT " + IncidentColumns + " FROM incidents WHERE tenant_id = @tenant AND edge_event_id = @edge";

    private const string ListSql =
        "SELECT " + IncidentColumns + " FROM incidents WHERE tenant_id = @tenant ORDER BY created_at, id LIMIT @limit OFFSET @offset";

    private const string DeadLetterSql =
        "SELECT id AS \"Id\", tenant_id AS \"TenantId\", type AS \"Type\", payload::text AS \"Payload\", " +
        "attempts AS \"Attempts\", created_at AS \"CreatedAt\" FROM outbox " +
        "WHERE tenant_id = @tenant AND attempts >= 10 ORDER BY created_at LIMIT @limit";

    private const string InsertIncidentSql =
        "INSERT INTO incidents (tenant_id, id, campus_id, event_type, camera_id, edge_event_id, state, version, detected_at, rule_version, model_version) " +
        "VALUES (@tenant, @id, @campus, @event, @camera, @edge, @state, @version, @detectedAt, @ruleVersion, @modelVersion)";

    private const string ConditionalUpdateSql =
        "UPDATE incidents SET state = @state, version = @version " +
        "WHERE tenant_id = @tenant AND id = @id AND version = @expected";

    private const string InsertActivitySql =
        "INSERT INTO incident_activity (tenant_id, incident_id, actor_id, reason, occurred_at) " +
        "VALUES (@tenant, @incidentId, @actor, @reason, @occurredAt)";

    private const string InsertOutboxSql =
        "INSERT INTO outbox (tenant_id, type, payload) VALUES (@tenant, @type, @payload::jsonb)";
}
