using System.Security.Claims;
using EduSphere.Safety.Application;
using EduSphere.Safety.Core;
using EduSphere.Safety.Shared;

namespace EduSphere.Safety.Api;

public sealed record IngestRequest(
    string EdgeEventId,
    string CampusId,
    string EventType,
    string CameraId,
    DateTimeOffset DetectedAtUtc,
    string RuleVersion,
    string ModelVersion);

public sealed record TransitionRequest(string Next, string? Reason);

public sealed record DevTokenRequest(string TenantId, string UserId);

public static class IncidentsEndpoints
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/v1/ingest/events", PostIngestAsync);
        var incidents = app.MapGroup("/v1/incidents").RequireAuthorization();
        incidents.MapGet("/", ListIncidentsAsync);
        incidents.MapGet("/{id:guid}", GetIncidentAsync);
        incidents.MapPatch("/{id:guid}", PatchIncidentAsync);
        app.MapGet("/v1/ops/dead-letter", GetDeadLetterAsync).RequireAuthorization();
        var auth = app.Services.GetRequiredService<IConfiguration>().GetSection(AuthOptions.SectionName).Get<AuthOptions>();
        // DEV STUB ONLY: minted HS256 tokens for local testing. Never mapped in
        // production (Auth:DevStubEnabled defaults false).
        if (auth?.DevStubEnabled == true)
            app.MapPost("/v1/auth/dev-token", PostDevTokenAsync);
        app.MapHub<SafetyHub>("/safety");
    }

    public static async Task<IResult> PostIngestAsync(
        HttpContext http,
        IngestRequest body,
        ISender sender,
        IIncidentStore store,
        IDeviceStore devices,
        IIncidentBroadcaster broadcast,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(body);
        if (!DeviceAuth.TryParseHeader(http.Request.Headers[DeviceAuth.HeaderName], out var deviceId, out var secret))
            return Results.Json(new { code = "device.unauthorized", message = "Missing or malformed device key." }, statusCode: 401);
        var device = await devices.FindByIdAsync(deviceId, ct);
        if (device is null || device.Revoked || !DeviceAuth.SecretMatches(secret, device.KeyHash))
            return Results.Json(new { code = "device.unauthorized", message = "Unknown, revoked, or invalid device key." }, statusCode: 401);

        // Idempotency: same (tenant, edgeEventId) returns the existing id with
        // no new outbox row and no repeat broadcast. The fast path above covers
        // the already-ingested case; a concurrent duplicate that slips past it
        // is resolved inside the store — AddAsync catches the unique-violation
        // (23505), writes NO second outbox row, and returns the winner's id —
        // so duplicate ingests never 500.
        var existing = await store.FindByEdgeEventAsync(device.TenantId, body.EdgeEventId, ct);
        if (existing is not null)
            return Results.Json(new { id = existing.Id });

        var command = new CreateIncidentCommand(
            body.EdgeEventId, device.TenantId, body.CampusId, body.EventType,
            body.CameraId, body.DetectedAtUtc, body.RuleVersion, body.ModelVersion);
        var result = await sender.SendAsync(command, ct);
        if (!result.IsSuccess)
            return ErrorMapper.ToResult(result.Error!);
        await broadcast.IncidentCreatedAsync(device.TenantId, body.CampusId, new { incidentId = result.Value }, ct);
        return Results.Json(new { id = result.Value });
    }

    public static async Task<IResult> ListIncidentsAsync(
        HttpContext http,
        IIncidentStore store,
        IDeviceStore devices,
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default)
    {
        var tenant = await ResolveTenantAsync(http, devices, ct);
        if (!tenant.IsSuccess)
            return tenant.Failure!;
        var incidents = await store.ListAsync(
            tenant.TenantId!, Math.Clamp(limit, 1, 200), Math.Max(offset, 0), ct);
        return Results.Json(new
        {
            items = incidents.Select(ToDto).ToList(),
            limit = Math.Clamp(limit, 1, 200),
            offset = Math.Max(offset, 0),
        });
    }

    public static async Task<IResult> GetIncidentAsync(
        HttpContext http,
        Guid id,
        IIncidentStore store,
        IDeviceStore devices,
        CancellationToken ct)
    {
        var tenant = await ResolveTenantAsync(http, devices, ct);
        if (!tenant.IsSuccess)
            return tenant.Failure!;
        var incident = await store.GetAsync(tenant.TenantId!, id, ct);
        // Other-tenant ids are indistinguishable from missing: 404 either way.
        if (incident is null)
            return ErrorMapper.ToResult(new Error("incident.not_found", "Incident not found."));
        http.Response.Headers["ETag"] = $"W/\"{incident.Version}\"";
        return Results.Json(ToDto(incident));
    }

    public static async Task<IResult> PatchIncidentAsync(
        HttpContext http,
        Guid id,
        TransitionRequest body,
        ISender sender,
        IIncidentStore store,
        IDeviceStore devices,
        IIncidentBroadcaster broadcast,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var tenant = await ResolveTenantAsync(http, devices, ct);
        if (!tenant.IsSuccess)
            return tenant.Failure!;
        var rawIfMatch = http.Request.Headers["If-Match"].ToString();
        if (string.IsNullOrWhiteSpace(rawIfMatch))
            return Results.Json(
                new { code = "incident.if_match_required", message = "PATCH requires an If-Match header with the current version." },
                statusCode: StatusCodes.Status428PreconditionRequired);
        if (!TryParseIfMatch(rawIfMatch, out var expectedVersion))
            return ErrorMapper.ToResult(new Error("validation.failed", "If-Match must be a version like W/\"3\" or \"3\"."));
        if (!Enum.TryParse<IncidentState>(body.Next, ignoreCase: true, out var next))
            return ErrorMapper.ToResult(new Error("validation.failed", $"Unknown state '{body.Next}'."));
        var actor = Actor(http);
        var result = await sender.SendAsync(
            new TransitionIncidentCommand(id, tenant.TenantId!, expectedVersion, next, actor, body.Reason), ct);
        if (!result.IsSuccess)
            return ErrorMapper.ToResult(result.Error!);
        var reloaded = await store.GetAsync(tenant.TenantId!, id, ct);
        if (reloaded is not null)
        {
            http.Response.Headers["ETag"] = $"W/\"{reloaded.Version}\"";
            await broadcast.IncidentUpdatedAsync(
                tenant.TenantId!, reloaded.CampusId, new { incidentId = id, version = reloaded.Version }, ct);
            return Results.Json(new { id, version = reloaded.Version });
        }

        return Results.Json(new { id });
    }

    public static async Task<IResult> GetDeadLetterAsync(
        HttpContext http,
        IIncidentStore store,
        IDeviceStore devices,
        CancellationToken ct)
    {
        var tenant = await ResolveTenantAsync(http, devices, ct);
        if (!tenant.IsSuccess)
            return tenant.Failure!;
        var rows = await store.ListDeadLetterAsync(tenant.TenantId!, 100, ct);
        return Results.Json(new
        {
            items = rows.Select(r => new
            {
                id = r.Id,
                tenantId = r.TenantId,
                type = r.Type,
                payload = r.Payload,
                attempts = r.Attempts,
                createdAt = r.CreatedAt,
            }).ToList(),
        });
    }

    public static IResult PostDevTokenAsync(DevTokenRequest body, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(body);
        var auth = config.GetSection(AuthOptions.SectionName).Get<AuthOptions>();
        if (auth?.DevStubEnabled != true)
            return Results.NotFound();
        if (string.IsNullOrWhiteSpace(body.TenantId) || string.IsNullOrWhiteSpace(body.UserId))
            return ErrorMapper.ToResult(new Error("validation.failed", "tenantId and userId are required."));
        var token = DevTokenIssuer.Mint(auth.IssuerSigningKey, auth.Issuer, auth.Audience, body.TenantId, body.UserId);
        return Results.Json(new { accessToken = token, tokenType = "Bearer" });
    }

    public static bool TryParseIfMatch(string raw, out int version)
    {
        version = 0;
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("W/", StringComparison.Ordinal))
            trimmed = trimmed[2..].Trim();
        trimmed = trimmed.Trim('"');
        // Multiple ETags (If-Match list) are not supported in Plan 1.
        return trimmed.Length > 0 && !trimmed.Contains(',') && int.TryParse(trimmed, out version) && version >= 0;
    }

    private sealed record TenantResolution(bool IsSuccess, string? TenantId, IResult? Failure);

    private static async Task<TenantResolution> ResolveTenantAsync(HttpContext http, IDeviceStore devices, CancellationToken ct)
    {
        // Tenant ALWAYS comes from the verified tid claim, never a header.
        var tenantId = http.User.FindFirst("tid")?.Value;
        var actor = Actor(http);
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(actor))
            return new TenantResolution(false, null, Results.Json(
                new { code = "auth.tenant_required", message = "Token must carry tid and sub claims." }, statusCode: 401));
        if (!await devices.IsTenantMemberAsync(tenantId, actor, ct))
            return new TenantResolution(false, null, Results.Json(
                new { code = "tenant.not_member", message = "Caller is not a member of this tenant." }, statusCode: 403));
        return new TenantResolution(true, tenantId, null);
    }

    private static object ToDto(Incident incident) => new
    {
        id = incident.Id,
        tenantId = incident.TenantId,
        campusId = incident.CampusId,
        eventType = incident.EventType,
        cameraId = incident.CameraId,
        state = incident.State.ToString(),
        version = incident.Version,
    };

    private static string Actor(HttpContext http) =>
        http.User.FindFirst("sub")?.Value
        ?? http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? string.Empty;
}
