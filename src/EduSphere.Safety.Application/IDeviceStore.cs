namespace EduSphere.Safety.Application;

public sealed record DeviceRecord(string TenantId, string DeviceId, string KeyHash, bool Revoked);

/// <summary>
/// Device + membership lookups backing Task 5 auth. Devices PK is
/// (tenant_id, id) but pre-auth the tenant is unknown, so lookup is by id
/// alone (full scan; the table is small — acceptable for the pilot).
/// key_hash is lowercase hex of SHA256(secret UTF-8 bytes).
/// </summary>
public interface IDeviceStore
{
    Task<DeviceRecord?> FindByIdAsync(string deviceId, CancellationToken ct);

    Task<bool> IsTenantMemberAsync(string tenantId, string userId, CancellationToken ct);
}
