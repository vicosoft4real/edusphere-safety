namespace EduSphere.Safety.Application;

public sealed record BundleRecord(
    string TenantId, int Version, string Document, string CreatedBy, DateTimeOffset CreatedAt);

public sealed record PolicyRecord(
    string TenantId, string Id, string LevelsJson, string RecipientsJson,
    string CreatedBy, DateTimeOffset CreatedAt);

public sealed record ActiveConfig(
    string TenantId, int BundleVersion, string BundleDocument,
    IReadOnlyList<PolicyRecord> Policies);

public interface IConfigStore
{
    /// <summary>
    /// Inserts the bundle and bumps the tenant version counter in one
    /// transaction; returns the assigned version.
    /// </summary>
    Task<int> PublishBundleAsync(string tenantId, string document, string createdBy, CancellationToken ct);

    Task<BundleRecord?> GetLatestBundleAsync(string tenantId, CancellationToken ct);

    Task<PolicyRecord?> GetPolicyAsync(string tenantId, string id, CancellationToken ct);

    Task SetPolicyAsync(PolicyRecord policy, CancellationToken ct);

    Task<IReadOnlyList<PolicyRecord>> ListPoliciesAsync(string tenantId, CancellationToken ct);

    Task<ActiveConfig?> GetActiveConfigAsync(string tenantId, CancellationToken ct);
}
