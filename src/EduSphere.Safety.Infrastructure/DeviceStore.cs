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
                "SELECT tenant_id AS \"TenantId\", id AS \"DeviceId\", key_hash AS \"KeyHash\", revoked AS \"Revoked\" FROM devices WHERE id = @id",
                new { id = deviceId },
                cancellationToken: ct));
        return row is null ? null : new DeviceRecord(row.TenantId, row.DeviceId, row.KeyHash, row.Revoked);
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
    }
}
