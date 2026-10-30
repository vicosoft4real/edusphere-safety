using System.Security.Claims;
using EduSphere.Safety.Application;
using EduSphere.Safety.Shared;

namespace EduSphere.Safety.Api;

public sealed record CameraRequest(
    string CampusId,
    string? DeviceId,
    string Mode,
    string Host,
    int Port,
    string Path);

public sealed record CameraTestReport(bool Ok, string Detail);

public static class CameraEndpoints
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var cameras = app.MapGroup("/v1/cameras").RequireAuthorization();
        cameras.MapPost("/", PostCameraAsync);
        cameras.MapPut("/{id}", PutCameraAsync);
        cameras.MapDelete("/{id}", DeleteCameraAsync);
        cameras.MapGet("/", ListCamerasAsync);
        // Edge-reported connectivity: device auth, camera must belong to the
        // device tenant or the report is indistinguishable from missing (404).
        app.MapPost("/v1/cameras/{id}/test-report", PostTestReportAsync);
    }

    public static async Task<IResult> PostCameraAsync(
        HttpContext http,
        CameraRequest body,
        ISender sender,
        IDeviceStore devices,
        ICameraStore cameras,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var tenant = await ResolveTenantAsync(http, devices, ct);
        if (!tenant.IsSuccess)
            return tenant.Failure!;
        var result = await sender.SendAsync(
            new CreateCameraCommand(
                tenant.TenantId!, body.CampusId, body.DeviceId,
                body.Mode ?? string.Empty, body.Host ?? string.Empty, body.Port, body.Path ?? string.Empty),
            ct);
        if (!result.IsSuccess)
            return ErrorMapper.ToResult(result.Error!);
        return Results.Json(new { id = result.Value });
    }

    public static async Task<IResult> PutCameraAsync(
        HttpContext http,
        string id,
        CameraRequest body,
        ISender sender,
        IDeviceStore devices,
        ICameraStore cameras,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var tenant = await ResolveTenantAsync(http, devices, ct);
        if (!tenant.IsSuccess)
            return tenant.Failure!;
        var result = await sender.SendAsync(
            new ReplaceCameraCommand(
                tenant.TenantId!, id, body.CampusId, body.DeviceId,
                body.Mode ?? string.Empty, body.Host ?? string.Empty, body.Port, body.Path ?? string.Empty),
            ct);
        if (!result.IsSuccess)
            return ErrorMapper.ToResult(result.Error!);
        return Results.Json(new { id });
    }

    public static async Task<IResult> DeleteCameraAsync(
        HttpContext http,
        string id,
        ISender sender,
        IDeviceStore devices,
        ICameraStore cameras,
        CancellationToken ct)
    {
        var tenant = await ResolveTenantAsync(http, devices, ct);
        if (!tenant.IsSuccess)
            return tenant.Failure!;
        var result = await sender.SendAsync(new DeleteCameraCommand(tenant.TenantId!, id), ct);
        if (!result.IsSuccess)
            return ErrorMapper.ToResult(result.Error!);
        return Results.Json(new { id, deleted = true });
    }

    public static async Task<IResult> ListCamerasAsync(
        HttpContext http,
        IDeviceStore devices,
        ICameraStore cameras,
        CancellationToken ct)
    {
        var tenant = await ResolveTenantAsync(http, devices, ct);
        if (!tenant.IsSuccess)
            return tenant.Failure!;
        var list = await cameras.ListAsync(tenant.TenantId!, ct);
        // Diagnostics carry descriptors only; no credentials are stored, so
        // none can leak here by construction.
        return Results.Json(new
        {
            items = list.Select(c => new
            {
                id = c.CameraId,
                tenantId = c.TenantId,
                campusId = c.CampusId,
                deviceId = c.DeviceId,
                mode = c.Mode,
                host = c.Host,
                port = c.Port,
                path = c.Path,
                lastTestOk = c.LastTestOk,
                lastTestDetail = c.LastTestDetail,
                lastTestAt = c.LastTestAt,
            }).ToList(),
        });
    }

    public static async Task<IResult> PostTestReportAsync(
        HttpContext http,
        string id,
        CameraTestReport body,
        ISender sender,
        IDeviceStore devices,
        ICameraStore cameras,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!DeviceAuth.TryParseHeader(http.Request.Headers[DeviceAuth.HeaderName], out var deviceId, out var secret))
            return Results.Json(new { code = "device.unauthorized", message = "Missing or malformed device key." }, statusCode: 401);
        var device = await devices.FindByIdAsync(deviceId, ct);
        if (device is null || device.Revoked || !DeviceAuth.SecretMatches(secret, device.KeyHash))
            return Results.Json(new { code = "device.unauthorized", message = "Unknown, revoked, or invalid device key." }, statusCode: 401);
        var result = await sender.SendAsync(
            new ReportCameraTestCommand(device.TenantId, id, body.Ok, body.Detail ?? string.Empty), ct);
        if (!result.IsSuccess)
            return ErrorMapper.ToResult(result.Error!);
        return Results.Json(new { id, ok = body.Ok });
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
