namespace EduSphere.Safety.Application;

public sealed record CameraRecord(
    string TenantId,
    string CameraId,
    string CampusId,
    string? DeviceId,
    string Mode,
    string Host,
    int Port,
    string Path,
    bool? LastTestOk = null,
    string LastTestDetail = "",
    DateTimeOffset? LastTestAt = null);

public interface ICameraStore
{
    Task InsertAsync(CameraRecord camera, CancellationToken ct);

    Task<bool> ReplaceAsync(CameraRecord camera, CancellationToken ct);

    Task<bool> DeleteAsync(string tenantId, string cameraId, CancellationToken ct);

    Task<CameraRecord?> FindAsync(string tenantId, string cameraId, CancellationToken ct);

    Task<IReadOnlyList<CameraRecord>> ListAsync(string tenantId, CancellationToken ct);

    Task<bool> ReportTestAsync(
        string tenantId, string cameraId, bool ok, string detail, DateTimeOffset at, CancellationToken ct);
}
