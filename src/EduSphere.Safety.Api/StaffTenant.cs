using System.Security.Claims;
using EduSphere.Safety.Application;

namespace EduSphere.Safety.Api;

/// <summary>
/// Staff (OIDC) tenant resolution shared by endpoint groups: the bearer token
/// must carry <c>tid</c> + <c>sub</c> claims and the caller must be a member of
/// the tenant. Never trusts a client-supplied tenant header.
/// </summary>
public static class StaffTenant
{
    public sealed record Resolution(bool IsSuccess, string? TenantId, string? Actor, IResult? Failure);

    public static async Task<Resolution> ResolveAsync(HttpContext http, IDeviceStore devices, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(devices);
        var tenantId = http.User.FindFirst("tid")?.Value;
        var actor = http.User.FindFirst("sub")?.Value
            ?? http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? string.Empty;
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(actor))
            return new Resolution(false, null, null, Results.Json(
                new { code = "auth.tenant_required", message = "Token must carry tid and sub claims." }, statusCode: 401));
        if (!await devices.IsTenantMemberAsync(tenantId, actor, ct))
            return new Resolution(false, null, null, Results.Json(
                new { code = "tenant.not_member", message = "Caller is not a member of this tenant." }, statusCode: 403));
        return new Resolution(true, tenantId, actor, null);
    }
}
