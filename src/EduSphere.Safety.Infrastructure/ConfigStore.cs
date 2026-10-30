using Dapper;
using EduSphere.Safety.Application;
using Npgsql;

namespace EduSphere.Safety.Infrastructure;

public sealed class ConfigStore(PostgresOptions options) : IConfigStore
{
    private readonly PostgresOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task<int> PublishBundleAsync(string tenantId, string document, string createdBy, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        // Counter bump + bundle insert in one transaction: concurrent publishes
        // serialize on the tenant row, so versions never collide or skip.
        // The state row is seeded on first publish (pre-004 tenants included).
        await connection.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO tenant_config_state (tenant_id, bundle_version) VALUES (@tenant, 0) ON CONFLICT DO NOTHING",
                new { tenant = tenantId },
                transaction: tx,
                cancellationToken: ct));
        var current = await connection.QuerySingleAsync<int>(
            new CommandDefinition(
                "SELECT bundle_version FROM tenant_config_state WHERE tenant_id = @tenant FOR UPDATE",
                new { tenant = tenantId },
                transaction: tx,
                cancellationToken: ct));
        var version = current + 1;
        await connection.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO rule_bundles (tenant_id, version, document, created_by) VALUES (@tenant, @version, @document::jsonb, @by)",
                new { tenant = tenantId, version, document, by = createdBy },
                transaction: tx,
                cancellationToken: ct));
        await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE tenant_config_state SET bundle_version = @version WHERE tenant_id = @tenant",
                new { tenant = tenantId, version },
                transaction: tx,
                cancellationToken: ct));
        await tx.CommitAsync(ct);
        return version;
    }

    public async Task<PolicyRecord?> GetPolicyAsync(string tenantId, string id, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<PolicyRow>(
            new CommandDefinition(
                "SELECT tenant_id AS \"TenantId\", id AS \"Id\", levels::text AS \"LevelsJson\", recipients::text AS \"RecipientsJson\", created_by AS \"CreatedBy\", created_at AS \"CreatedAt\" FROM notification_policies WHERE tenant_id = @tenant AND id = @id",
                new { tenant = tenantId, id },
                cancellationToken: ct));
        return row is null
            ? null
            : new PolicyRecord(row.TenantId, row.Id, row.LevelsJson, row.RecipientsJson, row.CreatedBy, row.CreatedAt);
    }

    public async Task<BundleRecord?> GetLatestBundleAsync(string tenantId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<BundleRow>(
            new CommandDefinition(
                "SELECT tenant_id AS \"TenantId\", version AS \"Version\", document::text AS \"Document\", created_by AS \"CreatedBy\", created_at AS \"CreatedAt\" FROM rule_bundles WHERE tenant_id = @tenant ORDER BY version DESC LIMIT 1",
                new { tenant = tenantId },
                cancellationToken: ct));
        return row is null ? null : new BundleRecord(row.TenantId, row.Version, row.Document, row.CreatedBy, row.CreatedAt);
    }

    public async Task SetPolicyAsync(PolicyRecord policy, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO notification_policies (tenant_id, id, levels, recipients, created_by) VALUES (@tenant, @id, @levels::jsonb, @recipients::jsonb, @by) ON CONFLICT (tenant_id, id) DO UPDATE SET levels = EXCLUDED.levels, recipients = EXCLUDED.recipients, created_by = EXCLUDED.created_by, created_at = now()",
                new
                {
                    tenant = policy.TenantId,
                    id = policy.Id,
                    levels = policy.LevelsJson,
                    recipients = policy.RecipientsJson,
                    by = policy.CreatedBy,
                },
                cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PolicyRecord>> ListPoliciesAsync(string tenantId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(ct);
        var rows = await connection.QueryAsync<PolicyRow>(
            new CommandDefinition(
                "SELECT tenant_id AS \"TenantId\", id AS \"Id\", levels::text AS \"LevelsJson\", recipients::text AS \"RecipientsJson\", created_by AS \"CreatedBy\", created_at AS \"CreatedAt\" FROM notification_policies WHERE tenant_id = @tenant ORDER BY id",
                new { tenant = tenantId },
                cancellationToken: ct));
        return rows.Select(r => new PolicyRecord(r.TenantId, r.Id, r.LevelsJson, r.RecipientsJson, r.CreatedBy, r.CreatedAt)).ToList();
    }

    public async Task<ActiveConfig?> GetActiveConfigAsync(string tenantId, CancellationToken ct)
    {
        var bundle = await GetLatestBundleAsync(tenantId, ct);
        if (bundle is null)
            return null;
        return new ActiveConfig(tenantId, bundle.Version, bundle.Document, await ListPoliciesAsync(tenantId, ct));
    }

    private string RequireConnectionString() =>
        string.IsNullOrWhiteSpace(_options.ConnectionString)
            ? throw new InvalidOperationException("PostgresOptions.ConnectionString is not configured.")
            : _options.ConnectionString;

    private sealed class BundleRow
    {
        public string TenantId { get; set; } = string.Empty;
        public int Version { get; set; }
        public string Document { get; set; } = string.Empty;
        public string CreatedBy { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
    }

    private sealed class PolicyRow
    {
        public string TenantId { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string LevelsJson { get; set; } = string.Empty;
        public string RecipientsJson { get; set; } = string.Empty;
        public string CreatedBy { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
    }
}
