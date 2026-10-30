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

public sealed class CreateIncidentHandler(
    IIncidentStore store,
    INotificationStore notifications,
    IConfigStore policies) : ICommandHandler<CreateIncidentCommand, Guid>
{
    public async Task<Result<Guid>> HandleAsync(CreateIncidentCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var validation = await new CreateIncidentCommandValidator().ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Result<Guid>.Fail("validation.failed", string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));

        var existing = await store.FindByEdgeEventAsync(command.TenantId, command.EdgeEventId, ct);
        if (existing is not null)
        {
            // Duplicate delivery (edge retry after timeout, not a new event):
            // the original creation may have crashed before scheduling, so
            // re-ensure the level-1 row idempotently instead of skipping.
            await ScheduleFirstEscalationAsync(command, existing.Id, ct);
            return Result<Guid>.Ok(existing.Id);
        }

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
        // Lost-race path inside AddAsync returns the winner's id; the
        // idempotent schedule insert converges both racers on one row with an
        // identical due_at (derived from the same edge event).
        await ScheduleFirstEscalationAsync(command, effectiveId, ct);
        return Result<Guid>.Ok(effectiveId);
    }

    private async Task ScheduleFirstEscalationAsync(CreateIncidentCommand command, Guid incidentId, CancellationToken ct)
    {
        var policy = await policies.GetPolicyAsync(command.TenantId, NotificationPolicy.DefaultId, ct);
        if (policy is null)
            return;
        var levels = NotificationLevels.Parse(policy.LevelsJson);
        if (levels.Count == 0)
            return;
        await notifications.ScheduleFirstAsync(
            command.TenantId, incidentId, 1, command.DetectedAtUtc.AddSeconds(levels[0].AfterSeconds), ct);
    }
}
