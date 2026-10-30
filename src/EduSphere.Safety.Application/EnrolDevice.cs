using EduSphere.Safety.Shared;
using FluentValidation;

namespace EduSphere.Safety.Application;

public static class EnrolmentPolicy
{
    public const int CodeTtlMinutes = 15;
    public const int CodeBytes = 16;
    public const int SecretBytes = 32;
    public const int DeviceIdBytes = 9;
}

public sealed record EnrolGrant(string TenantId, string? CampusId);

public sealed record CreateEnrolCodeCommand(string TenantId, string? CampusId, string RequestedBy) : ICommand<string>;

public sealed class CreateEnrolCodeCommandValidator : AbstractValidator<CreateEnrolCodeCommand>
{
    public CreateEnrolCodeCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.RequestedBy).NotEmpty();
    }
}

public sealed class CreateEnrolCodeHandler(IDeviceStore store) : ICommandHandler<CreateEnrolCodeCommand, string>
{
    public async Task<Result<string>> HandleAsync(CreateEnrolCodeCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var validation = await new CreateEnrolCodeCommandValidator().ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Result<string>.Fail("validation.failed", string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));

        var code = Secrets.NewUrlSafeSecret(EnrolmentPolicy.CodeBytes);
        await store.CreateEnrolCodeAsync(
            command.TenantId,
            command.CampusId,
            Secrets.Sha256Hex(code),
            DateTimeOffset.UtcNow.AddMinutes(EnrolmentPolicy.CodeTtlMinutes),
            command.RequestedBy,
            ct);
        return Result<string>.Ok(code);
    }
}

public sealed record EnrolDeviceCommand(string Code) : ICommand<EnrolResult>;

public sealed record EnrolResult(string DeviceId, string Secret);

public sealed class EnrolDeviceCommandValidator : AbstractValidator<EnrolDeviceCommand>
{
    public EnrolDeviceCommandValidator()
    {
        RuleFor(x => x.Code).NotEmpty();
    }
}

public sealed class EnrolDeviceHandler(IDeviceStore store) : ICommandHandler<EnrolDeviceCommand, EnrolResult>
{
    public async Task<Result<EnrolResult>> HandleAsync(EnrolDeviceCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var validation = await new EnrolDeviceCommandValidator().ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Result<EnrolResult>.Fail("validation.failed", string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));

        var grant = await store.ConsumeEnrolCodeAsync(Secrets.Sha256Hex(command.Code.Trim()), DateTimeOffset.UtcNow, ct);
        if (grant is null)
            return Result<EnrolResult>.Fail("device.enrol_invalid", "Enrolment code is unknown, expired, or already used.");

        var deviceId = "dev-" + Secrets.NewUrlSafeId(EnrolmentPolicy.DeviceIdBytes);
        var secret = Secrets.NewUrlSafeSecret(EnrolmentPolicy.SecretBytes);
        await store.RegisterDeviceAsync(
            new DeviceRecord(grant.TenantId, deviceId, Secrets.Sha256Hex(secret), false, grant.CampusId), ct);
        return Result<EnrolResult>.Ok(new EnrolResult(deviceId, secret));
    }
}

public sealed record RotateDeviceSecretCommand(string TenantId, string DeviceId) : ICommand<string>;

public sealed class RotateDeviceSecretCommandValidator : AbstractValidator<RotateDeviceSecretCommand>
{
    public RotateDeviceSecretCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.DeviceId).NotEmpty();
    }
}

public sealed class RotateDeviceSecretHandler(IDeviceStore store) : ICommandHandler<RotateDeviceSecretCommand, string>
{
    public async Task<Result<string>> HandleAsync(RotateDeviceSecretCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var validation = await new RotateDeviceSecretCommandValidator().ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Result<string>.Fail("validation.failed", string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));

        var secret = Secrets.NewUrlSafeSecret(EnrolmentPolicy.SecretBytes);
        var rotated = await store.RotateSecretAsync(command.TenantId, command.DeviceId, Secrets.Sha256Hex(secret), ct);
        if (!rotated)
            return Result<string>.Fail("device.not_found", "Device not found.");
        return Result<string>.Ok(secret);
    }
}

public sealed record RevokeDeviceCommand(string TenantId, string DeviceId, bool Revoked) : ICommand<bool>;

public sealed class RevokeDeviceHandler(IDeviceStore store) : ICommandHandler<RevokeDeviceCommand, bool>
{
    public async Task<Result<bool>> HandleAsync(RevokeDeviceCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.TenantId) || string.IsNullOrWhiteSpace(command.DeviceId))
            return Result<bool>.Fail("validation.failed", "TenantId and DeviceId are required.");
        var updated = await store.SetRevokedAsync(command.TenantId, command.DeviceId, command.Revoked, ct);
        if (!updated)
            return Result<bool>.Fail("device.not_found", "Device not found.");
        return Result<bool>.Ok(command.Revoked);
    }
}
