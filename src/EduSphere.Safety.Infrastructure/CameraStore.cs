using Dapper;
using EduSphere.Safety.Application;
using Npgsql;

namespace EduSphere.Safety.Infrastructure;

public sealed class CameraStore(PostgresOptions options) : ICameraStore
{
    private readonly PostgresOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task InsertAsync(CameraRecord camera, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO cameras (tenant_id, id, campus_id, device_id, mode, host, port, path) VALUES (@tenant, @id, @campus, @device, @mode, @host, @port, @path)",
                new
                {
                    tenant = camera.TenantId,
                    id = camera.CameraId,
                    campus = camera.CampusId,
                    device = camera.DeviceId,
                    mode = camera.Mode,
                    host = camera.Host,
                    port = camera.Port,
                    path = camera.Path,
                },
                cancellationToken: ct));
    }

    public async Task<bool> ReplaceAsync(CameraRecord camera, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(ct);
        var rows = await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE cameras SET campus_id = @campus, device_id = @device, mode = @mode, host = @host, port = @port, path = @path WHERE tenant_id = @tenant AND id = @id",
                new
                {
                    tenant = camera.TenantId,
                    id = camera.CameraId,
                    campus = camera.CampusId,
                    device = camera.DeviceId,
                    mode = camera.Mode,
                    host = camera.Host,
                    port = camera.Port,
                    path = camera.Path,
                },
                cancellationToken: ct));
        return rows == 1;
    }

    public async Task<bool> DeleteAsync(string tenantId, string cameraId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(ct);
        var rows = await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM cameras WHERE tenant_id = @tenant AND id = @id",
                new { tenant = tenantId, id = cameraId },
                cancellationToken: ct));
        return rows == 1;
    }

    public async Task<CameraRecord?> FindAsync(string tenantId, string cameraId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<CameraRow>(
            new CommandDefinition(
                Select() + " WHERE tenant_id = @tenant AND id = @id",
                new { tenant = tenantId, id = cameraId },
                cancellationToken: ct));
        return row is null ? null : row.ToRecord();
    }

    public async Task<IReadOnlyList<CameraRecord>> ListAsync(string tenantId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(ct);
        var rows = await connection.QueryAsync<CameraRow>(
            new CommandDefinition(
                Select() + " WHERE tenant_id = @tenant ORDER BY id",
                new { tenant = tenantId },
                cancellationToken: ct));
        return rows.Select(r => r.ToRecord()).ToList();
    }

    public async Task<bool> ReportTestAsync(
        string tenantId, string cameraId, bool ok, string detail, DateTimeOffset at, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(ct);
        var rows = await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE cameras SET last_test_ok = @ok, last_test_detail = @detail, last_test_at = @at WHERE tenant_id = @tenant AND id = @id",
                new { ok, detail, at, tenant = tenantId, id = cameraId },
                cancellationToken: ct));
        return rows == 1;
    }

    private static string Select() =>
        "SELECT tenant_id AS \"TenantId\", id AS \"CameraId\", campus_id AS \"CampusId\", device_id AS \"DeviceId\", mode AS \"Mode\", host AS \"Host\", port AS \"Port\", path AS \"Path\", last_test_ok AS \"LastTestOk\", last_test_detail AS \"LastTestDetail\", last_test_at AS \"LastTestAt\" FROM cameras";

    private string RequireConnectionString() =>
        string.IsNullOrWhiteSpace(_options.ConnectionString)
            ? throw new InvalidOperationException("PostgresOptions.ConnectionString is not configured.")
            : _options.ConnectionString;

    private sealed class CameraRow
    {
        public string TenantId { get; set; } = string.Empty;
        public string CameraId { get; set; } = string.Empty;
        public string CampusId { get; set; } = string.Empty;
        public string? DeviceId { get; set; }
        public string Mode { get; set; } = string.Empty;
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public string Path { get; set; } = string.Empty;
        public bool? LastTestOk { get; set; }
        public string LastTestDetail { get; set; } = string.Empty;
        public DateTimeOffset? LastTestAt { get; set; }

        public CameraRecord ToRecord() => new(
            TenantId, CameraId, CampusId, DeviceId, Mode, Host, Port, Path,
            LastTestOk, LastTestDetail, LastTestAt);
    }
}
