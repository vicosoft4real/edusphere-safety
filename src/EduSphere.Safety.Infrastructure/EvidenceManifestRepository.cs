using Dapper;
using EduSphere.Safety.Application;
using Npgsql;

namespace EduSphere.Safety.Infrastructure;

/// <summary>
/// Dapper persistence for evidence manifests + access log (migration 005).
/// Shared by both storage backends so the SQL exists exactly once.
/// </summary>
internal sealed class EvidenceManifestRepository(PostgresOptions options)
{
    private readonly PostgresOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task RegisterAsync(EvidenceManifest manifest, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(ConnectionString());
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO evidence_manifests (tenant_id, edge_event_id, incident_id, object_key, kind, bytes, sha256, uploaded_at, expires_at) VALUES (@tenant, @edge, @incident, @key, @kind, @bytes, @sha, @uploaded, @expires) ON CONFLICT (tenant_id, edge_event_id, kind) DO UPDATE SET object_key = EXCLUDED.object_key, bytes = EXCLUDED.bytes, sha256 = EXCLUDED.sha256, uploaded_at = EXCLUDED.uploaded_at, expires_at = EXCLUDED.expires_at",
                new
                {
                    tenant = manifest.TenantId,
                    edge = manifest.EdgeEventId,
                    incident = manifest.IncidentId,
                    key = manifest.ObjectKey,
                    kind = manifest.Kind,
                    bytes = manifest.Bytes,
                    sha = manifest.Sha256,
                    uploaded = manifest.UploadedAt,
                    expires = manifest.ExpiresAt,
                },
                cancellationToken: ct));
    }

    public async Task<EvidenceManifest?> FindAsync(string tenantId, string edgeEventId, string kind, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(ConnectionString());
        await connection.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<ManifestRow>(
            new CommandDefinition(
                "SELECT tenant_id AS \"TenantId\", edge_event_id AS \"EdgeEventId\", incident_id AS \"IncidentId\", object_key AS \"ObjectKey\", kind AS \"Kind\", bytes AS \"Bytes\", sha256 AS \"Sha256\", uploaded_at AS \"UploadedAt\", expires_at AS \"ExpiresAt\", created_at AS \"CreatedAt\" FROM evidence_manifests WHERE tenant_id = @tenant AND edge_event_id = @edge AND kind = @kind",
                new { tenant = tenantId, edge = edgeEventId, kind },
                cancellationToken: ct));
        if (row is null)
            return null;
        // The manifest table carries no campus/camera columns by design:
        // recover them from the server-built key layout (fixed segments,
        // restricted charset), else leave them empty.
        var (campus, camera) = SplitKey(row.ObjectKey);

        return new EvidenceManifest(
            row.TenantId, row.EdgeEventId, row.IncidentId, row.ObjectKey, row.Kind,
            campus, camera, row.Bytes, row.Sha256, row.UploadedAt, row.ExpiresAt, row.CreatedAt);
    }

    private static (string Campus, string Camera) SplitKey(string objectKey)
    {
        var keyParts = (objectKey ?? string.Empty).Split('/');
        if (keyParts.Length == 5
            && ObjectKeys.TrySplitFileName(keyParts[4], out _, out _))
            return (keyParts[1], keyParts[2]);
        return (string.Empty, string.Empty);
    }

    public async Task<bool> MarkUploadedAsync(string tenantId, string edgeEventId, string kind, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(ConnectionString());
        await connection.OpenAsync(ct);
        var updated = await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE evidence_manifests SET uploaded_at = now() WHERE tenant_id = @tenant AND edge_event_id = @edge AND kind = @kind",
                new { tenant = tenantId, edge = edgeEventId, kind },
                cancellationToken: ct));
        return updated == 1;
    }

    public async Task LogAccessAsync(AccessLogRow row, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(ConnectionString());
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO evidence_access_log (tenant_id, manifest_tenant, object_key, actor_id) VALUES (@tenant, @owner, @key, @actor)",
                new
                {
                    tenant = row.TenantId,
                    owner = row.ManifestTenant,
                    key = row.ObjectKey,
                    actor = row.ActorId,
                },
                cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AccessLogRow>> ListAccessLogAsync(string tenantId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(ConnectionString());
        await connection.OpenAsync(ct);
        var rows = await connection.QueryAsync<AccessRow>(
            new CommandDefinition(
                "SELECT tenant_id AS \"TenantId\", manifest_tenant AS \"ManifestTenant\", object_key AS \"ObjectKey\", actor_id AS \"ActorId\", accessed_at AS \"AccessedAt\" FROM evidence_access_log WHERE tenant_id = @tenant ORDER BY accessed_at",
                new { tenant = tenantId },
                cancellationToken: ct));
        return rows.Select(r => new AccessLogRow(r.TenantId, r.ManifestTenant, r.ObjectKey, r.ActorId, r.AccessedAt)).ToList();
    }

    public async Task<IReadOnlyList<EvidenceManifest>> ListExpiredAsync(
        DateTimeOffset clipsOlderThanUtc, DateTimeOffset snapshotsOlderThanUtc, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(ConnectionString());
        await connection.OpenAsync(ct);
        var rows = await connection.QueryAsync<ManifestRow>(
            new CommandDefinition(
                "SELECT tenant_id AS \"TenantId\", edge_event_id AS \"EdgeEventId\", incident_id AS \"IncidentId\", object_key AS \"ObjectKey\", kind AS \"Kind\", bytes AS \"Bytes\", sha256 AS \"Sha256\", uploaded_at AS \"UploadedAt\", expires_at AS \"ExpiresAt\", created_at AS \"CreatedAt\" FROM evidence_manifests WHERE uploaded_at IS NOT NULL AND ((kind = 'clip' AND uploaded_at <= @clip) OR (kind = 'snapshot' AND uploaded_at <= @snap)) ORDER BY uploaded_at LIMIT 500",
                new { clip = clipsOlderThanUtc, snap = snapshotsOlderThanUtc },
                cancellationToken: ct));
        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<EvidenceManifest>> ListAbandonedAsync(
        DateTimeOffset uploadExpiredBeforeUtc, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(ConnectionString());
        await connection.OpenAsync(ct);
        var rows = await connection.QueryAsync<ManifestRow>(
            new CommandDefinition(
                "SELECT tenant_id AS \"TenantId\", edge_event_id AS \"EdgeEventId\", incident_id AS \"IncidentId\", object_key AS \"ObjectKey\", kind AS \"Kind\", bytes AS \"Bytes\", sha256 AS \"Sha256\", uploaded_at AS \"UploadedAt\", expires_at AS \"ExpiresAt\", created_at AS \"CreatedAt\" FROM evidence_manifests WHERE uploaded_at IS NULL AND expires_at IS NOT NULL AND expires_at <= @before ORDER BY expires_at LIMIT 500",
                new { before = uploadExpiredBeforeUtc },
                cancellationToken: ct));
        return rows.Select(Map).ToList();
    }

    public async Task DeleteManifestAsync(string tenantId, string edgeEventId, string kind, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(ConnectionString());
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM evidence_manifests WHERE tenant_id = @tenant AND edge_event_id = @edge AND kind = @kind",
                new { tenant = tenantId, edge = edgeEventId, kind },
                cancellationToken: ct));
    }

    public async Task<int> PurgeAccessLogAsync(DateTimeOffset accessedBeforeUtc, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(ConnectionString());
        await connection.OpenAsync(ct);
        return await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM evidence_access_log WHERE accessed_at < @before",
                new { before = accessedBeforeUtc },
                cancellationToken: ct));
    }

    private static EvidenceManifest Map(ManifestRow row)
    {
        var (campus, camera) = SplitKey(row.ObjectKey);
        return new EvidenceManifest(
            row.TenantId, row.EdgeEventId, row.IncidentId, row.ObjectKey, row.Kind,
            campus, camera, row.Bytes, row.Sha256, row.UploadedAt, row.ExpiresAt, row.CreatedAt);
    }

    private string ConnectionString() =>
        string.IsNullOrWhiteSpace(_options.ConnectionString)
            ? throw new InvalidOperationException("PostgresOptions.ConnectionString is not configured.")
            : _options.ConnectionString;

    private sealed class ManifestRow
    {
        public string TenantId { get; set; } = string.Empty;
        public string EdgeEventId { get; set; } = string.Empty;
        public Guid? IncidentId { get; set; }
        public string ObjectKey { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public long Bytes { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public DateTimeOffset? UploadedAt { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    private sealed class AccessRow
    {
        public string TenantId { get; set; } = string.Empty;
        public string ManifestTenant { get; set; } = string.Empty;
        public string ObjectKey { get; set; } = string.Empty;
        public string ActorId { get; set; } = string.Empty;
        public DateTimeOffset AccessedAt { get; set; }
    }
}
