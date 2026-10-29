using System.Text.Json;
using EduSphere.Safety.Core;
using EduSphere.Safety.Shared;
using FluentValidation;

namespace EduSphere.Safety.Application;

public sealed record CreateIncidentCommand(
    string EdgeEventId,
    string TenantId,
    string CampusId,
    string EventType,
    string CameraId,
    DateTimeOffset DetectedAtUtc,
    string RuleVersion,
    string ModelVersion) : ICommand<Guid>;

public sealed class CreateIncidentCommandValidator : AbstractValidator<CreateIncidentCommand>
{
    public CreateIncidentCommandValidator()
    {
        RuleFor(x => x.EdgeEventId).NotEmpty();
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.CampusId).NotEmpty();
        RuleFor(x => x.EventType).NotEmpty();
        RuleFor(x => x.CameraId).NotEmpty();
        RuleFor(x => x.DetectedAtUtc)
            .Must(d => d != default && d <= DateTimeOffset.UtcNow.AddMinutes(5))
            .WithMessage("DetectedAtUtc must be set and not more than 5 minutes in the future.");
        RuleFor(x => x.RuleVersion).NotEmpty();
        RuleFor(x => x.ModelVersion).NotEmpty();
    }
}

public sealed class CreateIncidentHandler(IIncidentStore store) : ICommandHandler<CreateIncidentCommand, Guid>
{
    public async Task<Result<Guid>> HandleAsync(CreateIncidentCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var validation = await new CreateIncidentCommandValidator().ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Result<Guid>.Fail("validation.failed", string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));

        var existing = await store.FindByEdgeEventAsync(command.TenantId, command.EdgeEventId, ct);
        if (existing is not null)
            return Result<Guid>.Ok(existing.Id);

        var incident = Incident.Create(
            command.TenantId, command.CampusId, command.EventType, command.CameraId, command.EdgeEventId);
        var payload = JsonSerializer.Serialize(new
        {
            incidentId = incident.Id,
            edgeEventId = command.EdgeEventId,
            tenantId = command.TenantId,
            campusId = command.CampusId,
            eventType = command.EventType,
            cameraId = command.CameraId,
            detectedAtUtc = command.DetectedAtUtc,
            ruleVersion = command.RuleVersion,
            modelVersion = command.ModelVersion,
        });
        var effectiveId = await store.AddAsync(incident, new OutboxEntry(command.TenantId, "incident.created", payload), ct);
        return Result<Guid>.Ok(effectiveId);
    }
}
