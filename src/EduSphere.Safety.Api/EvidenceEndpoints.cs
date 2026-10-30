using System.Security.Cryptography;
using EduSphere.Safety.Application;
using EduSphere.Safety.Infrastructure;
using EduSphere.Safety.Shared;

namespace EduSphere.Safety.Api;

public sealed record ManifestRequest(
    string CampusId, string CameraId, string EdgeEventId, string Kind, long Bytes, string Sha256);

public sealed record CompleteRequest(string EdgeEventId, string Kind);

public static class EvidenceEndpoints
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        // Device-authenticated edge calls (no OIDC; the device key is the credential).
        app.MapPost("/v1/evidence/manifest", PostManifestAsync);
        app.MapPost("/v1/evidence/manifest/complete", PostCompleteAsync);
        // File-backend mediated transfer (HMAC token in query is the credential).
        app.MapPut("/v1/evidence/file-upload", PutFileUploadAsync);
        app.MapGet("/v1/evidence/file-download", GetFileDownloadAsync);

        var staff = app.MapGroup("/v1/evidence").RequireAuthorization();
        staff.MapGet("/access-log", GetAccessLogAsync);
        staff.MapGet("/{edgeEventId}/{kind}", GetPlaybackAsync);
    }

    public static async Task<IResult> PostManifestAsync(
        HttpContext http,
        ManifestRequest body,
        ISender sender,
        IDeviceStore devices,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var device = await AuthenticateDeviceAsync(http, devices, ct);
        if (device is null)
            return Results.Json(new { code = "device.unauthorized", message = "Unknown, revoked, or invalid device key." }, statusCode: 401);
        var result = await sender.SendAsync(
            new RegisterManifestCommand(
                device.TenantId, device.CampusId, body.CampusId ?? string.Empty,
                body.CameraId ?? string.Empty, body.EdgeEventId ?? string.Empty,
                body.Kind ?? string.Empty, body.Bytes, body.Sha256 ?? string.Empty),
            ct);
        if (!result.IsSuccess)
            return ErrorMapper.ToResult(result.Error!);
        return Results.Json(new
        {
            objectKey = result.Value!.ObjectKey,
            putUrl = result.Value!.PutUrl,
            expiresAt = result.Value!.ExpiresAtUtc,
        });
    }

    public static async Task<IResult> PostCompleteAsync(
        HttpContext http,
        CompleteRequest body,
        ISender sender,
        IDeviceStore devices,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var device = await AuthenticateDeviceAsync(http, devices, ct);
        if (device is null)
            return Results.Json(new { code = "device.unauthorized", message = "Unknown, revoked, or invalid device key." }, statusCode: 401);
        var result = await sender.SendAsync(
            new CompleteManifestCommand(device.TenantId, body.EdgeEventId ?? string.Empty, body.Kind ?? string.Empty), ct);
        if (!result.IsSuccess)
            return ErrorMapper.ToResult(result.Error!);
        return Results.Json(new { uploaded = true });
    }

    public static async Task<IResult> GetAccessLogAsync(
        HttpContext http,
        IEvidenceStore store,
        IDeviceStore devices,
        CancellationToken ct)
    {
        var tenant = await StaffTenant.ResolveAsync(http, devices, ct);
        if (!tenant.IsSuccess)
            return tenant.Failure!;
        var rows = await store.ListAccessLogAsync(tenant.TenantId!, ct);
        return Results.Json(new
        {
            items = rows.Select(r => new
            {
                objectKey = r.ObjectKey,
                actorId = r.ActorId,
                accessedAt = r.AccessedAt,
            }).ToList(),
        });
    }

    public static async Task<IResult> GetPlaybackAsync(
        HttpContext http,
        string edgeEventId,
        string kind,
        ISender sender,
        IDeviceStore devices,
        CancellationToken ct)
    {
        var tenant = await StaffTenant.ResolveAsync(http, devices, ct);
        if (!tenant.IsSuccess)
            return tenant.Failure!;
        var result = await sender.SendAsync(
            new ResolvePlaybackCommand(tenant.TenantId!, tenant.Actor!, edgeEventId, kind), ct);
        if (!result.IsSuccess)
            return ErrorMapper.ToResult(result.Error!);
        return Results.Redirect(result.Value!.GetUrl);
    }

    public static async Task<IResult> PutFileUploadAsync(
        HttpContext http,
        IEvidenceStore store,
        CancellationToken ct)
    {
        if (store is not FileEvidenceStore files)
            return Results.Json(
                new { code = "storage.backend_mismatch", message = "Direct upload goes to presigned S3 PUT URLs on this backend." },
                statusCode: 400);
        var token = http.Request.Query["token"].FirstOrDefault();
        if (!files.TryValidateFileToken(token ?? string.Empty, FileEvidenceStore.UploadPurpose, out var tenant, out var key))
            return Results.Json(new { code = "evidence.token_invalid", message = "Upload token is invalid or expired." }, statusCode: 401);
        var fileName = key.Split('/')[^1];
        if (!ObjectKeys.TrySplitFileName(fileName, out var edgeEventId, out var kind))
            return Results.Json(new { code = "evidence.not_found", message = "No such evidence manifest." }, statusCode: 404);
        var manifest = await store.FindManifestAsync(tenant, edgeEventId, kind, ct);
        if (manifest is null || !string.Equals(manifest.ObjectKey, key, StringComparison.Ordinal))
            return Results.Json(new { code = "evidence.not_found", message = "No such evidence manifest." }, statusCode: 404);

        using var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, ct);
        if (buffer.Length != manifest.Bytes)
            return Results.Json(new { code = "evidence.size_mismatch", message = "Uploaded bytes do not match the manifest." }, statusCode: 400);
        var bytes = buffer.ToArray();
        if (!Secrets.FixedTimeEqualsHex(Sha256Hex(bytes), manifest.Sha256))
            return Results.Json(new { code = "evidence.sha_mismatch", message = "Uploaded bytes do not match the manifest hash." }, statusCode: 400);

        var path = files.ResolvePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, ct);
        await store.MarkUploadedAsync(tenant, edgeEventId, kind, ct);
        return Results.Json(new { uploaded = true, objectKey = key });
    }

    public static async Task<IResult> GetFileDownloadAsync(
        HttpContext http,
        IEvidenceStore store,
        CancellationToken ct)
    {
        if (store is not FileEvidenceStore files)
            return Results.Json(
                new { code = "storage.backend_mismatch", message = "Downloads go through presigned S3 GET URLs on this backend." },
                statusCode: 400);
        var token = http.Request.Query["token"].FirstOrDefault();
        if (!files.TryValidateFileToken(token ?? string.Empty, FileEvidenceStore.DownloadPurpose, out var tenant, out var key))
            return Results.Json(new { code = "evidence.token_invalid", message = "Download token is invalid or expired." }, statusCode: 401);
        if (!key.StartsWith(tenant + "/", StringComparison.Ordinal))
            return Results.Json(new { code = "evidence.not_found", message = "No such evidence." }, statusCode: 404);
        var path = files.ResolvePath(key);
        if (!File.Exists(path))
            return Results.Json(new { code = "evidence.not_found", message = "Evidence bytes are not uploaded yet." }, statusCode: 404);
        var bytes = await File.ReadAllBytesAsync(path, ct);
        return Results.File(bytes, ContentTypeFor(key));
    }

    private static async Task<DeviceRecord?> AuthenticateDeviceAsync(HttpContext http, IDeviceStore devices, CancellationToken ct)
    {
        if (!DeviceAuth.TryParseHeader(http.Request.Headers[DeviceAuth.HeaderName], out var deviceId, out var secret))
            return null;
        var device = await devices.FindByIdAsync(deviceId, ct);
        if (device is null || device.Revoked || !DeviceAuth.SecretMatches(secret, device.KeyHash))
            return null;
        return device;
    }

    private static string ContentTypeFor(string objectKey) =>
        objectKey.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? "video/mp4" : "image/jpeg";

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
