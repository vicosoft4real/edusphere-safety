using EduSphere.Safety.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace EduSphere.Safety.Application;

/// <summary>
/// Shared kernel registration used by both Api and Worker:
/// mediator <see cref="ISender"/> (Transient — handlers use scoped services,
/// so Sender must never be a Singleton resolved from the root provider)
/// plus the Plan 1 command-handler registrations.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSharedKernel(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddTransient<ISender, Sender>();
        services.AddTransient<ICommandHandler<CreateIncidentCommand, Guid>, CreateIncidentHandler>();
        services.AddTransient<ICommandHandler<TransitionIncidentCommand, Guid>, TransitionIncidentHandler>();
        services.AddTransient<ICommandHandler<CreateEnrolCodeCommand, string>, CreateEnrolCodeHandler>();
        services.AddTransient<ICommandHandler<EnrolDeviceCommand, EnrolResult>, EnrolDeviceHandler>();
        services.AddTransient<ICommandHandler<RotateDeviceSecretCommand, string>, RotateDeviceSecretHandler>();
        services.AddTransient<ICommandHandler<RevokeDeviceCommand, bool>, RevokeDeviceHandler>();
        return services;
    }
}
