using System.Security.Claims;
using System.Text.Json;
using EduSphere.Safety.Application;
using EduSphere.Safety.Shared;

namespace EduSphere.Safety.Api;

public sealed record CreateEnrolCodeRequest(string? CampusId);

public sealed record EnrolRequest(string Code);

public sealed record RevokeRequest(bool Revoked);

public static class DeviceEndpoints
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var devices = app.MapGroup("/v1/devices").RequireAuthorization();
        devices.MapPost("/enrol-codes", PostEnrolCodeAsync);
        devices.MapGet("/", ListDevicesAsync);
        devices.MapPost("/{id}/revoke", PostRevokeAsync);
        // Enrol is code-gated, not bearer-gated: the single-use short-lived
        // code IS the credential. Rate limiting is CI-hardening (noted, not claimed).
        app.MapPost("/v1/devices/enrol", PostEnrolAsync);
        app.MapPost("/v1/devices/rotate", PostRotateAsync);
        app.MapGet("/v1/devices/config", GetConfigAsync);
        app.MapPost("/v1/devices/heartbeat", PostHeartbeatAsync);
    }

    public static async Task<IResult> PostEnrolCodeAsync(
        HttpContext http,
        CreateEnrolCodeRequest body,
        ISender sender,
        IDeviceStore devices,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var tenant = await ResolveTenantAsync(http, devices, ct);
        if (!tenant.IsSuccess)
            return tenant.Failure!;
        var result = await sender.SendAsync(
            new CreateEnrolCodeCommand(tenant.TenantId!, body.CampusId, tenant.Actor!), ct);
        if (!result.IsSuccess)
            return ErrorMapper.ToResult(result.Error!);
        return Results.Json(new
        {
            code = result.Value,
            expiresInSeconds = EnrolmentPolicy.CodeTtlMinutes * 60,
        });
    }

    public static async Task<IResult> PostEnrolAsync(
        EnrolRequest body,
        ISender sender,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await sender.SendAsync(new EnrolDeviceCommand(body.Code ?? string.Empty), ct);
        if (!result.IsSuccess)
            return ErrorMapper.ToResult(result.Error!);
        // The plaintext secret is returned exactly once, here. It is never
        // stored, logged, or returned again by any endpoint.
        return Results.Json(new { deviceId = result.Value!.DeviceId, secret = result.Value!.Secret });
    }

    public static async Task<IResult> PostRotateAsync(
        HttpContext http,
        ISender sender,
        IDeviceStore devices,
        CancellationToken ct)
    {
        if (!DeviceAuth.TryParseHeader(http.Request.Headers[DeviceAuth.HeaderName], out var deviceId, out var secret))
            return Results.Json(new { code = "device.unauthorized", message = "Missing or malformed device key." }, statusCode: 401);
        var device = await devices.FindByIdAsync(deviceId, ct);
        if (device is null || device.Revoked || !DeviceAuth.SecretMatches(secret, device.KeyHash))
            return Results.Json(new { code = "device.unauthorized", message = "Unknown, revoked, or invalid device key." }, statusCode: 401);
        var result = await sender.SendAsync(new RotateDeviceSecretCommand(device.TenantId, device.DeviceId), ct);
        if (!result.IsSuccess)
            return ErrorMapper.ToResult(result.Error!);
        return Results.Json(new { deviceId = device.DeviceId, secret = result.Value! });
    }

    public static async Task<IResult> PostRevokeAsync(
        HttpContext http,
        string id,
        RevokeRequest body,
        ISender sender,
        IDeviceStore devices,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var tenant = await ResolveTenantAsync(http, devices, ct);
        if (!tenant.IsSuccess)
            return tenant.Failure!;
        var result = await sender.SendAsync(new RevokeDeviceCommand(tenant.TenantId!, id, body.Revoked), ct);
        if (!result.IsSuccess)
            return ErrorMapper.ToResult(result.Error!);
        return Results.Json(new { id, revoked = result.Value });
    }

    public static async Task<IResult> GetConfigAsync(
        HttpContext http,
        IDeviceStore devices,
        IConfigStore config,
        CancellationToken ct)
    {
        if (!DeviceAuth.TryParseHeader(http.Request.Headers[DeviceAuth.HeaderName], out var deviceId, out var secret))
            return Results.Json(new { code = "device.unauthorized", message = "Missing or malformed device key." }, statusCode: 401);
        var device = await devices.FindByIdAsync(deviceId, ct);
        if (device is null || device.Revoked || !DeviceAuth.SecretMatches(secret, device.KeyHash))
            return Results.Json(new { code = "device.unauthorized", message = "Unknown, revoked, or invalid device key." }, statusCode: 401);
        var active = await config.GetActiveConfigAsync(device.TenantId, ct);
        if (active is null)
            return Results.Json(new { code = "config.no_bundle", message = "No bundle published." }, statusCode: 404);
        var etag = $"W/\"{active.BundleVersion}\"";
        if (http.Request.Headers.IfNoneMatch.Any(v => v is not null && v.Trim() == etag))
            return Results.StatusCode(StatusCodes.Status304NotModified);
        http.Response.Headers.ETag = etag;
        return Results.Json(new
        {
            bundleVersion = active.BundleVersion,
            bundle = JsonSerializer.Deserialize<JsonElement>(active.BundleDocument),
            policies = active.Policies.Select(p => new
            {
                id = p.Id,
                levels = JsonSerializer.Deserialize<JsonElement>(p.LevelsJson),
                recipients = JsonSerializer.Deserialize<JsonElement>(p.RecipientsJson),
            }).ToList(),
        });
    }

    public static async Task<IResult> ListDevicesAsync(
        HttpContext http,
        IDeviceStore devices,
        IConfigStore config,
        CancellationToken ct)
    {
        var tenant = await ResolveTenantAsync(http, devices, ct);
        if (!tenant.IsSuccess)
            return tenant.Failure!;
        var list = await devices.ListAsync(tenant.TenantId!, ct);
        var active = await config.GetLatestBundleAsync(tenant.TenantId!, ct);
        var serverVersion = active?.Version;
        // Key material never leaves the server: no secret, no hash in the DTO.
        return Results.Json(new
        {
            items = list.Select(d => new
            {
                id = d.DeviceId,
                tenantId = d.TenantId,
                campusId = d.CampusId,
                revoked = d.Revoked,
                softwareVersion = d.SoftwareVersion,
                modelVersion = d.ModelVersion,
                configVersion = d.ConfigVersion,
                lastHeartbeatAt = d.LastHeartbeatAt,
                staleConfig = serverVersion.HasValue && d.ConfigVersion < serverVersion.Value,
            }).ToList(),
        });
    }

    public sealed record HeartbeatRequest(
        string SoftwareVersion,
        string ModelVersion,
        int ConfigVersion,
        int QueueDepth,
        double FrameAgeSeconds,
        long DiskUsedBytes);

    public static async Task<IResult> PostHeartbeatAsync(
        HttpContext http,
        HeartbeatRequest body,
        IDeviceStore devices,
        IConfigStore config,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!DeviceAuth.TryParseHeader(http.Request.Headers[DeviceAuth.HeaderName], out var deviceId, out var secret))
            return Results.Json(new { code = "device.unauthorized", message = "Missing or malformed device key." }, statusCode: 401);
        var device = await devices.FindByIdAsync(deviceId, ct);
        if (device is null || device.Revoked || !DeviceAuth.SecretMatches(secret, device.KeyHash))
            return Results.Json(new { code = "device.unauthorized", message = "Unknown, revoked, or invalid device key." }, statusCode: 401);
        var problem = ValidateHeartbeat(body);
        if (problem is not null)
            return ErrorMapper.ToResult(new Error("validation.failed", problem));
        await devices.RecordHeartbeatAsync(
            device.TenantId, device.DeviceId,
            body.SoftwareVersion, body.ModelVersion, body.ConfigVersion, ct);
        var active = await config.GetLatestBundleAsync(device.TenantId, ct);
        int? serverVersion = active?.Version;
        return Results.Json(new
        {
            serverConfigVersion = serverVersion,
            configStale = serverVersion.HasValue && body.ConfigVersion < serverVersion.Value,
            serverTimeUtc = DateTimeOffset.UtcNow,
        });
    }

    private static string? ValidateHeartbeat(HeartbeatRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.SoftwareVersion))
            return "SoftwareVersion is required.";
        if (string.IsNullOrWhiteSpace(body.ModelVersion))
            return "ModelVersion is required.";
        if (body.ConfigVersion < 0)
            return "ConfigVersion must be >= 0.";
        if (body.QueueDepth < 0)
            return "QueueDepth must be >= 0.";
        if (double.IsNaN(body.FrameAgeSeconds) || double.IsInfinity(body.FrameAgeSeconds) || body.FrameAgeSeconds < 0)
            return "FrameAgeSeconds must be a finite value >= 0.";
        if (body.DiskUsedBytes < 0)
            return "DiskUsedBytes must be >= 0.";
        return null;
    }

    private sealed record TenantResolution(bool IsSuccess, string? TenantId, string? Actor, IResult? Failure);

    private static async Task<TenantResolution> ResolveTenantAsync(HttpContext http, IDeviceStore devices, CancellationToken ct)
    {
        // Tenant ALWAYS comes from the verified tid claim, never a header.
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
