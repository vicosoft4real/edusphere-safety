using System.Text.RegularExpressions;
using EduSphere.Safety.Shared;
using FluentValidation;

namespace EduSphere.Safety.Application;

public static class EvidencePolicy
{
    public const int PutUrlTtlMinutes = 15;
    public const int GetUrlTtlMinutes = 5;
    public const long MaxBytes = 1_073_741_824;

    public static readonly IReadOnlySet<string> Kinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "clip", "snapshot",
    };

    public static string ExtensionFor(string kind) => kind switch
    {
        "clip" => ".clip.mp4",
        "snapshot" => ".snapshot.jpg",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown evidence kind."),
    };
}

public static class ObjectKeys
{
    /// <summary>
    /// Builds the tenant-scoped object key
    /// <c>{tenant}/{campus}/{camera}/{yyyy-MM-dd}/{edgeEventId}.{clip.mp4|snapshot.jpg}</c>.
    /// Keys are never access control: every read/write goes through a signed URL or token.
    /// </summary>
    public static string Build(
        string tenantId, string campusId, string cameraId, DateOnly date, string edgeEventId, string kind) =>
        $"{tenantId}/{campusId}/{cameraId}/{date:yyyy-MM-dd}/{edgeEventId}{EvidencePolicy.ExtensionFor(kind)}";

    public static bool TrySplitFileName(string fileName, out string edgeEventId, out string kind)
    {
        edgeEventId = string.Empty;
        kind = string.Empty;
        foreach (var k in EvidencePolicy.Kinds)
        {
            var suffix = EvidencePolicy.ExtensionFor(k);
            if (fileName.EndsWith(suffix, StringComparison.Ordinal))
            {
                edgeEventId = fileName[..^suffix.Length];
                kind = k;
                return true;
            }
        }

        return false;
    }
}

public sealed record EvidenceManifest(
    string TenantId,
    string EdgeEventId,
    Guid? IncidentId,
    string ObjectKey,
    string Kind,
    string CampusId,
    string CameraId,
    long Bytes,
    string Sha256,
    DateTimeOffset? UploadedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt);

public sealed record AccessLogRow(
    string TenantId,
    string ManifestTenant,
    string ObjectKey,
    string ActorId,
    DateTimeOffset AccessedAt);

public interface IEvidenceStore
{
    Task RegisterManifestAsync(EvidenceManifest manifest, CancellationToken ct);
    Task<EvidenceManifest?> FindManifestAsync(string tenantId, string edgeEventId, string kind, CancellationToken ct);
    Task<bool> MarkUploadedAsync(string tenantId, string edgeEventId, string kind, CancellationToken ct);
    Task LogAccessAsync(AccessLogRow row, CancellationToken ct);
    Task<IReadOnlyList<AccessLogRow>> ListAccessLogAsync(string tenantId, CancellationToken ct);
    Task<string> MintPutUrlAsync(string tenantId, string objectKey, DateTimeOffset expiresAtUtc, CancellationToken ct);
    Task<string> MintGetUrlAsync(string tenantId, string objectKey, DateTimeOffset expiresAtUtc, CancellationToken ct);
    Task DeleteAsync(string objectKey, CancellationToken ct);
    Task<bool> ExistsAsync(string objectKey, CancellationToken ct);
}

public sealed record RegisterManifestCommand(
    string TenantId,
    string? DeviceCampusId,
    string CampusId,
    string CameraId,
    string EdgeEventId,
    string Kind,
    long Bytes,
    string Sha256) : ICommand<ManifestResult>;

public sealed record ManifestResult(string ObjectKey, string PutUrl, DateTimeOffset ExpiresAtUtc);

public sealed class RegisterManifestCommandValidator : AbstractValidator<RegisterManifestCommand>
{
    public RegisterManifestCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.CampusId).NotEmpty().Matches("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$");
        RuleFor(x => x.CameraId).NotEmpty().Matches("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$");
        RuleFor(x => x.EdgeEventId).NotEmpty().Matches("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$");
        RuleFor(x => x.Kind).Must(k => EvidencePolicy.Kinds.Contains(k)).WithMessage("Kind must be 'clip' or 'snapshot'.");
        RuleFor(x => x.Bytes).InclusiveBetween(0, EvidencePolicy.MaxBytes);
        RuleFor(x => x.Sha256).Matches("^[0-9a-f]{64}$").WithMessage("Sha256 must be 64 lowercase hex characters.");
    }
}

public sealed class RegisterManifestHandler(IEvidenceStore store) : ICommandHandler<RegisterManifestCommand, ManifestResult>
{
    public async Task<Result<ManifestResult>> HandleAsync(RegisterManifestCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var validation = await new RegisterManifestCommandValidator().ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Result<ManifestResult>.Fail("validation.failed", string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        if (command.DeviceCampusId is not null && !string.Equals(command.DeviceCampusId, command.CampusId, StringComparison.Ordinal))
            return Result<ManifestResult>.Fail("device.campus_mismatch", "Device campus does not match the manifest campus.");

        var key = ObjectKeys.Build(
            command.TenantId, command.CampusId, command.CameraId,
            DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime),
            command.EdgeEventId, command.Kind);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(EvidencePolicy.PutUrlTtlMinutes);
        await store.RegisterManifestAsync(
            new EvidenceManifest(
                command.TenantId, command.EdgeEventId, null, key, command.Kind,
                command.CampusId, command.CameraId, command.Bytes, command.Sha256,
                null, expiresAt, DateTimeOffset.UtcNow),
            ct);
        var putUrl = await store.MintPutUrlAsync(command.TenantId, key, expiresAt, ct);
        return Result<ManifestResult>.Ok(new ManifestResult(key, putUrl, expiresAt));
    }
}

public sealed record CompleteManifestCommand(string TenantId, string EdgeEventId, string Kind) : ICommand<bool>;

public sealed class CompleteManifestHandler(IEvidenceStore store) : ICommandHandler<CompleteManifestCommand, bool>
{
    public async Task<Result<bool>> HandleAsync(CompleteManifestCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.TenantId)
            || string.IsNullOrWhiteSpace(command.EdgeEventId)
            || string.IsNullOrWhiteSpace(command.Kind))
            return Result<bool>.Fail("validation.failed", "TenantId, EdgeEventId and Kind are required.");
        var marked = await store.MarkUploadedAsync(command.TenantId, command.EdgeEventId, command.Kind, ct);
        if (!marked)
            return Result<bool>.Fail("evidence.not_found", "No such evidence manifest.");
        return Result<bool>.Ok(true);
    }
}

public sealed record ResolvePlaybackCommand(string TenantId, string ActorId, string EdgeEventId, string Kind)
    : ICommand<PlaybackResult>;

public sealed record PlaybackResult(string GetUrl, DateTimeOffset ExpiresAtUtc);

public sealed class ResolvePlaybackHandler(IEvidenceStore store) : ICommandHandler<ResolvePlaybackCommand, PlaybackResult>
{
    public async Task<Result<PlaybackResult>> HandleAsync(ResolvePlaybackCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.TenantId)
            || string.IsNullOrWhiteSpace(command.ActorId)
            || string.IsNullOrWhiteSpace(command.EdgeEventId)
            || string.IsNullOrWhiteSpace(command.Kind))
            return Result<PlaybackResult>.Fail("validation.failed", "TenantId, ActorId, EdgeEventId and Kind are required.");
        var manifest = await store.FindManifestAsync(command.TenantId, command.EdgeEventId, command.Kind, ct);
        if (manifest is null)
            return Result<PlaybackResult>.Fail("evidence.not_found", "No such evidence manifest.");
        await store.LogAccessAsync(
            new AccessLogRow(
                command.TenantId, manifest.TenantId, manifest.ObjectKey,
                command.ActorId, DateTimeOffset.UtcNow),
            ct);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(EvidencePolicy.GetUrlTtlMinutes);
        var url = await store.MintGetUrlAsync(command.TenantId, manifest.ObjectKey, expiresAt, ct);
        return Result<PlaybackResult>.Ok(new PlaybackResult(url, expiresAt));
    }
}
