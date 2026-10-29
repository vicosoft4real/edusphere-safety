using System.Text.Json;
using EduSphere.Safety.Core;
using EduSphere.Safety.Shared;
using FluentValidation;

namespace EduSphere.Safety.Application;

public sealed record TransitionIncidentCommand(
    Guid IncidentId,
    string TenantId,
    int ExpectedVersion,
    IncidentState Next,
    string ActorId,
    string? Reason) : ICommand<Guid>;

public sealed class TransitionIncidentCommandValidator : AbstractValidator<TransitionIncidentCommand>
{
    public TransitionIncidentCommandValidator()
    {
        RuleFor(x => x.IncidentId).NotEmpty();
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.ExpectedVersion).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Next).IsInEnum();
        RuleFor(x => x.ActorId).NotEmpty();
        // Dismiss reason stays enforced by Core.TransitionTo so failures keep the domain code.
    }
}

public sealed class TransitionIncidentHandler(IIncidentStore store) : ICommandHandler<TransitionIncidentCommand, Guid>
{
    public async Task<Result<Guid>> HandleAsync(TransitionIncidentCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var validation = await new TransitionIncidentCommandValidator().ValidateAsync(command, ct);
        if (!validation.IsValid)
            return Result<Guid>.Fail("validation.failed", string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));

        var incident = await store.GetAsync(command.TenantId, command.IncidentId, ct);
        if (incident is null)
            return Result<Guid>.Fail("incident.not_found", "Incident not found.");
        if (incident.Version != command.ExpectedVersion)
            return Result<Guid>.Fail("incident.concurrency_conflict", "Incident was modified concurrently.");

        var transition = incident.TransitionTo(command.Next, command.ActorId, command.Reason);
        if (!transition.IsSuccess)
            return Result<Guid>.Fail(
                transition.Error?.Code ?? "incident.invalid_transition",
                transition.Error?.Message ?? "Incident transition failed.");

        var activity = new ActivityEntry(command.ActorId, command.Reason, DateTimeOffset.UtcNow);
        var payload = JsonSerializer.Serialize(new
        {
            incidentId = command.IncidentId,
            tenantId = command.TenantId,
            next = command.Next.ToString(),
            actorId = command.ActorId,
            reason = command.Reason,
            expectedVersion = command.ExpectedVersion,
            version = incident.Version,
        });
        var saved = await store.SaveAsync(
            incident,
            command.ExpectedVersion,
            activity,
            new OutboxEntry(command.TenantId, "incident.updated", payload),
            ct);
        if (!saved)
            return Result<Guid>.Fail("incident.concurrency_conflict", "Incident was modified concurrently.");
        return Result<Guid>.Ok(command.IncidentId);
    }
}
