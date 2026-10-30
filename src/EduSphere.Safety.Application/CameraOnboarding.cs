using EduSphere.Safety.Edge;
using EduSphere.Safety.Shared;
using FluentValidation;

namespace EduSphere.Safety.Application;

public static class CameraPolicy
{
    public const int DefaultRtspPort = 554;
    public static readonly string[] Modes = ["manual", "onvif"];

    /// <summary>
    /// Builds the candidate RTSP URL from non-secret descriptor parts and
    /// enforces the edge URL policy (loopback/private literals only, no
    /// userinfo, no metadata hosts). Throws ArgumentException when rejected.
    /// </summary>
    public static string ComposeAndValidateUrl(string mode, string host, int port, string path)
    {
        if (!Modes.Contains(mode))
            throw new ArgumentException($"Unknown camera mode '{mode}'.", nameof(mode));
        if (string.IsNullOrWhiteSpace(host) || host.Contains('@'))
            throw new ArgumentException("Camera host must be a bare hostname without credentials.", nameof(host));
        if (path.Contains('@'))
            throw new ArgumentException("Camera path must not contain credentials.", nameof(path));
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be 1-65535.");
        if (!path.StartsWith('/'))
            throw new ArgumentException("Camera path must start with '/'.", nameof(path));
        var url = $"rtsp://{host}:{port}{path}";
        if (FfmpegFrameSource.ValidateSourceUrl(url) != FrameSourceMode.Rtsp)
            throw new ArgumentException("Only network RTSP cameras can be registered.", nameof(host));
        return url;
    }
}

public sealed record CreateCameraCommand(
    string TenantId, string CampusId, string? DeviceId,
    string Mode, string Host, int Port, string Path) : ICommand<string>;

public sealed class CreateCameraCommandValidator : AbstractValidator<CreateCameraCommand>
{
    public CreateCameraCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.CampusId).NotEmpty();
        RuleFor(x => x.Mode).Must(m => CameraPolicy.Modes.Contains(m));
        RuleFor(x => x.Host).NotEmpty();
        RuleFor(x => x.Port).InclusiveBetween(1, 65535);
        RuleFor(x => x.Path).Must(p => p.StartsWith('/'));
    }
}

public sealed class CreateCameraHandler(ICameraStore cameras, IDeviceStore devices)
    : ICommandHandler<CreateCameraCommand, string>
{
    public async Task<Result<string>> HandleAsync(CreateCameraCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var validation = await new CreateCameraCommandValidator().ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Result<string>.Fail("validation.failed", string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));

        if (command.DeviceId is not null)
        {
            var device = await devices.FindByIdAsync(command.DeviceId, ct);
            if (device is null || device.TenantId != command.TenantId)
                return Result<string>.Fail("device.not_found", "Device not found.");
        }

        try
        {
            CameraPolicy.ComposeAndValidateUrl(command.Mode, command.Host, command.Port, command.Path);
        }
        catch (ArgumentException ex)
        {
            return Result<string>.Fail("validation.failed", ex.Message);
        }

        var cameraId = "cam-" + Secrets.NewUrlSafeId(9);
        await cameras.InsertAsync(
            new CameraRecord(
                command.TenantId, cameraId, command.CampusId, command.DeviceId,
                command.Mode, command.Host, command.Port, command.Path),
            ct);
        return Result<string>.Ok(cameraId);
    }
}

public sealed record ReplaceCameraCommand(
    string TenantId, string CameraId, string CampusId, string? DeviceId,
    string Mode, string Host, int Port, string Path) : ICommand<bool>;

public sealed class ReplaceCameraHandler(ICameraStore cameras) : ICommandHandler<ReplaceCameraCommand, bool>
{
    public async Task<Result<bool>> HandleAsync(ReplaceCameraCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.TenantId) || string.IsNullOrWhiteSpace(command.CameraId))
            return Result<bool>.Fail("validation.failed", "TenantId and CameraId are required.");
        try
        {
            CameraPolicy.ComposeAndValidateUrl(command.Mode, command.Host, command.Port, command.Path);
        }
        catch (ArgumentException ex)
        {
            return Result<bool>.Fail("validation.failed", ex.Message);
        }

        var existing = await cameras.FindAsync(command.TenantId, command.CameraId, ct);
        if (existing is null)
            return Result<bool>.Fail("camera.not_found", "Camera not found.");
        var replaced = await cameras.ReplaceAsync(
            existing with
            {
                CampusId = command.CampusId,
                DeviceId = command.DeviceId,
                Mode = command.Mode,
                Host = command.Host,
                Port = command.Port,
                Path = command.Path,
            }, ct);
        if (!replaced)
            return Result<bool>.Fail("camera.not_found", "Camera not found.");
        return Result<bool>.Ok(true);
    }
}

public sealed record DeleteCameraCommand(string TenantId, string CameraId) : ICommand<bool>;

public sealed class DeleteCameraHandler(ICameraStore cameras) : ICommandHandler<DeleteCameraCommand, bool>
{
    public async Task<Result<bool>> HandleAsync(DeleteCameraCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var deleted = await cameras.DeleteAsync(command.TenantId, command.CameraId, ct);
        if (!deleted)
            return Result<bool>.Fail("camera.not_found", "Camera not found.");
        return Result<bool>.Ok(true);
    }
}

public sealed record ReportCameraTestCommand(string TenantId, string CameraId, bool Ok, string Detail) : ICommand<bool>;

public sealed class ReportCameraTestHandler(ICameraStore cameras) : ICommandHandler<ReportCameraTestCommand, bool>
{
    public async Task<Result<bool>> HandleAsync(ReportCameraTestCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var reported = await cameras.ReportTestAsync(
            command.TenantId, command.CameraId, command.Ok, command.Detail ?? string.Empty, DateTimeOffset.UtcNow, ct);
        if (!reported)
            return Result<bool>.Fail("camera.not_found", "Camera not found.");
        return Result<bool>.Ok(true);
    }
}
