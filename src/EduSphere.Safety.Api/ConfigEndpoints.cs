using System.Security.Claims;
using System.Text.Json;
using EduSphere.Safety.Application;
using EduSphere.Safety.Shared;

namespace EduSphere.Safety.Api;

public sealed record PublishBundleRequest(string Document);

public sealed record SetPolicyRequest(string Id, JsonElement Levels, JsonElement Recipients);

public static class ConfigEndpoints
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var config = app.MapGroup("/v1/config").RequireAuthorization();
        config.MapPost("/bundles", PostBundleAsync);
        config.MapGet("/bundles/latest", GetLatestBundleAsync);
        config.MapPost("/policies", PostPolicyAsync);
        config.MapGet("/policies", ListPoliciesAsync);
    }

    public static async Task<IResult> PostBundleAsync(
        HttpContext http,
        PublishBundleRequest body,
        ISender sender,
        IDeviceStore devices,
        IConfigStore config,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var tenant = await ResolveTenantAsync(http, devices, ct);
        if (!tenant.IsSuccess)
            return tenant.Failure!;
        var result = await sender.SendAsync(
            new PublishBundleCommand(tenant.TenantId!, body.Document ?? string.Empty, tenant.Actor!), ct);
        if (!result.IsSuccess)
            return ErrorMapper.ToResult(result.Error!);
        return Results.Json(new { version = result.Value });
    }

    public static async Task<IResult> GetLatestBundleAsync(
        HttpContext http,
        IDeviceStore devices,
        IConfigStore config,
        CancellationToken ct)
    {
        var tenant = await ResolveTenantAsync(http, devices, ct);
        if (!tenant.IsSuccess)
            return tenant.Failure!;
        var bundle = await config.GetLatestBundleAsync(tenant.TenantId!, ct);
        if (bundle is null)
            return Results.Json(new { code = "config.no_bundle", message = "No bundle published." }, statusCode: 404);
        return Results.Json(new { version = bundle.Version, document = bundle.Document });
    }

    public static async Task<IResult> PostPolicyAsync(
        HttpContext http,
        SetPolicyRequest body,
        ISender sender,
        IDeviceStore devices,
        IConfigStore config,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var tenant = await ResolveTenantAsync(http, devices, ct);
        if (!tenant.IsSuccess)
            return tenant.Failure!;
        var result = await sender.SendAsync(
            new SetPolicyCommand(
                tenant.TenantId!, body.Id ?? string.Empty,
                body.Levels.GetRawText(), body.Recipients.GetRawText(), tenant.Actor!),
            ct);
        if (!result.IsSuccess)
            return ErrorMapper.ToResult(result.Error!);
        return Results.Json(new { id = body.Id });
    }

    public static async Task<IResult> ListPoliciesAsync(
        HttpContext http,
        IDeviceStore devices,
        IConfigStore config,
        CancellationToken ct)
    {
        var tenant = await ResolveTenantAsync(http, devices, ct);
        if (!tenant.IsSuccess)
            return tenant.Failure!;
        var policies = await config.ListPoliciesAsync(tenant.TenantId!, ct);
        return Results.Json(new
        {
            items = policies.Select(p => new
            {
                id = p.Id,
                levels = JsonSerializer.Deserialize<JsonElement>(p.LevelsJson),
                recipients = JsonSerializer.Deserialize<JsonElement>(p.RecipientsJson),
            }).ToList(),
        });
    }

    private sealed record TenantResolution(bool IsSuccess, string? TenantId, string? Actor, IResult? Failure);

    private static async Task<TenantResolution> ResolveTenantAsync(HttpContext http, IDeviceStore devices, CancellationToken ct)
    {
        var tenantId = http.User.FindFirst("tid")?.Value;
        var actor = http.User.FindFirst("sub")?.Value
            ?? http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? string.Empty;
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(actor))
            return new TenantResolution(false, null, null, Results.Json(
                new { code = "auth.tenant_required", message = "Token must carry tid and sub claims." }, statusCode: 401));
        if (!await devices.IsTenantMemberAsync(tenantId, actor, ct))
            return new TenantResolution(false, null, null, Results.Json(
                new { code = "tenant.not_member", message = "Caller is not a member of this tenant." }, statusCode: 403));
        return new TenantResolution(true, tenantId, actor, null);
    }
}
