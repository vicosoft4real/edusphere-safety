namespace EduSphere.Safety.Application;

public sealed record DeviceRecord(
    string TenantId,
    string DeviceId,
    string KeyHash,
    bool Revoked,
    string? CampusId = null,
    string SoftwareVersion = "",
    string ModelVersion = "",
    int ConfigVersion = 0,
    DateTimeOffset? LastHeartbeatAt = null);

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

    Task CreateEnrolCodeAsync(
        string tenantId, string? campusId, string codeHash,
        DateTimeOffset expiresAt, string createdBy, CancellationToken ct);

    /// <summary>
    /// Atomically consumes a live code (marks used iff unused and unexpired)
    /// and returns its grant; null when unknown, expired, or already used.
    /// </summary>
    Task<EnrolGrant?> ConsumeEnrolCodeAsync(string codeHash, DateTimeOffset nowUtc, CancellationToken ct);

    Task RegisterDeviceAsync(DeviceRecord device, CancellationToken ct);

    Task<bool> RotateSecretAsync(string tenantId, string deviceId, string newKeyHash, CancellationToken ct);

    Task<bool> SetRevokedAsync(string tenantId, string deviceId, bool revoked, CancellationToken ct);

    Task<IReadOnlyList<DeviceRecord>> ListAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// Records a device heartbeat: single UPDATE of the 002 version columns
    /// plus <c>last_heartbeat_at</c>.
    /// </summary>
    Task RecordHeartbeatAsync(
        string tenantId, string deviceId,
        string softwareVersion, string modelVersion, int configVersion,
        CancellationToken ct);
}
