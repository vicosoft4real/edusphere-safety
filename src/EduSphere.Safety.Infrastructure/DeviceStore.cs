using Dapper;
using EduSphere.Safety.Application;
using Npgsql;

namespace EduSphere.Safety.Infrastructure;

public sealed class DeviceStore(PostgresOptions options) : IDeviceStore
{
    private readonly PostgresOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task<DeviceRecord?> FindByIdAsync(string deviceId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(ct);
        // Devices PK is (tenant_id, id); pre-auth the tenant is unknown so the
        // lookup is by id alone. Full scan is acceptable for the pilot table size.
        var row = await connection.QuerySingleOrDefaultAsync<DeviceRow>(
            new CommandDefinition(
                "SELECT tenant_id AS \"TenantId\", id AS \"DeviceId\", key_hash AS \"KeyHash\", revoked AS \"Revoked\", campus_id AS \"CampusId\", software_version AS \"SoftwareVersion\", model_version AS \"ModelVersion\", config_version AS \"ConfigVersion\", last_heartbeat_at AS \"LastHeartbeatAt\" FROM devices WHERE id = @id",
                new { id = deviceId },
                cancellationToken: ct));
        return row is null ? null : new DeviceRecord(row.TenantId, row.DeviceId, row.KeyHash, row.Revoked, row.CampusId, row.SoftwareVersion, row.ModelVersion, row.ConfigVersion, row.LastHeartbeatAt);
    }

    public async Task<bool> IsTenantMemberAsync(string tenantId, string userId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(ct);
        var count = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM tenant_members WHERE tenant_id = @tenant AND user_id = @user",
                new { tenant = tenantId, user = userId },
                cancellationToken: ct));
        return count > 0;
    }

    public async Task CreateEnrolCodeAsync(
        string tenantId, string? campusId, string codeHash,
        DateTimeOffset expiresAt, string createdBy, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO enrolment_codes (code_hash, tenant_id, campus_id, expires_at, created_by) VALUES (@hash, @tenant, @campus, @expires, @by)",
                new { hash = codeHash, tenant = tenantId, campus = campusId, expires = expiresAt, by = createdBy },
                cancellationToken: ct));
    }

    public async Task<EnrolGrant?> ConsumeEnrolCodeAsync(string codeHash, DateTimeOffset nowUtc, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(ct);
        // Single statement: the code is consumed iff unused and unexpired,
        // so two concurrent enrols can never both win.
        var row = await connection.QuerySingleOrDefaultAsync<GrantRow>(
            new CommandDefinition(
                "UPDATE enrolment_codes SET used_at = now() WHERE code_hash = @hash AND used_at IS NULL AND expires_at > @now RETURNING tenant_id AS \"TenantId\", campus_id AS \"CampusId\"",
                new { hash = codeHash, now = nowUtc },
                cancellationToken: ct));
        return row is null ? null : new EnrolGrant(row.TenantId, row.CampusId);
    }

    public async Task RegisterDeviceAsync(DeviceRecord device, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO devices (tenant_id, id, key_hash, revoked, campus_id) VALUES (@tenant, @id, @hash, @revoked, @campus)",
                new { tenant = device.TenantId, id = device.DeviceId, hash = device.KeyHash, revoked = device.Revoked, campus = device.CampusId },
                cancellationToken: ct));
    }

    public async Task<bool> RotateSecretAsync(string tenantId, string deviceId, string newKeyHash, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(ct);
        var rows = await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE devices SET key_hash = @hash WHERE tenant_id = @tenant AND id = @id",
                new { hash = newKeyHash, tenant = tenantId, id = deviceId },
                cancellationToken: ct));
        return rows == 1;
    }

    public async Task<bool> SetRevokedAsync(string tenantId, string deviceId, bool revoked, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(ct);
        var rows = await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE devices SET revoked = @revoked WHERE tenant_id = @tenant AND id = @id",
                new { revoked, tenant = tenantId, id = deviceId },
                cancellationToken: ct));
        return rows == 1;
    }

    public async Task<IReadOnlyList<DeviceRecord>> ListAsync(string tenantId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(ct);
        var rows = await connection.QueryAsync<DeviceRow>(
            new CommandDefinition(
                "SELECT tenant_id AS \"TenantId\", id AS \"DeviceId\", key_hash AS \"KeyHash\", revoked AS \"Revoked\", campus_id AS \"CampusId\", software_version AS \"SoftwareVersion\", model_version AS \"ModelVersion\", config_version AS \"ConfigVersion\", last_heartbeat_at AS \"LastHeartbeatAt\" FROM devices WHERE tenant_id = @tenant ORDER BY id",
                new { tenant = tenantId },
                cancellationToken: ct));
        return rows.Select(r => new DeviceRecord(r.TenantId, r.DeviceId, r.KeyHash, r.Revoked, r.CampusId, r.SoftwareVersion, r.ModelVersion, r.ConfigVersion, r.LastHeartbeatAt)).ToList();
    }

    public async Task RecordHeartbeatAsync(
        string tenantId, string deviceId,
        string softwareVersion, string modelVersion, int configVersion,
        CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE devices SET software_version = @sw, model_version = @model, config_version = @cfg, last_heartbeat_at = now() WHERE tenant_id = @tenant AND id = @id",
                new { sw = softwareVersion, model = modelVersion, cfg = configVersion, tenant = tenantId, id = deviceId },
                cancellationToken: ct));
    }

    private string RequireConnectionString() =>
        string.IsNullOrWhiteSpace(_options.ConnectionString)
            ? throw new InvalidOperationException("PostgresOptions.ConnectionString is not configured.")
            : _options.ConnectionString;

    private sealed class DeviceRow
    {
        public string TenantId { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public string KeyHash { get; set; } = string.Empty;
        public bool Revoked { get; set; }
        public string? CampusId { get; set; }
        public string SoftwareVersion { get; set; } = string.Empty;
        public string ModelVersion { get; set; } = string.Empty;
        public int ConfigVersion { get; set; }
        public DateTimeOffset? LastHeartbeatAt { get; set; }
    }

    private sealed class GrantRow
    {
        public string TenantId { get; set; } = string.Empty;
        public string? CampusId { get; set; }
    }
}
