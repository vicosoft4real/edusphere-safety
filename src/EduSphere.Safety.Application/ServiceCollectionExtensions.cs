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
        services.AddTransient<ICommandHandler<CreateCameraCommand, string>, CreateCameraHandler>();
        services.AddTransient<ICommandHandler<ReplaceCameraCommand, bool>, ReplaceCameraHandler>();
        services.AddTransient<ICommandHandler<DeleteCameraCommand, bool>, DeleteCameraHandler>();
        services.AddTransient<ICommandHandler<ReportCameraTestCommand, bool>, ReportCameraTestHandler>();
        services.AddTransient<ICommandHandler<PublishBundleCommand, int>, PublishBundleHandler>();
        services.AddTransient<ICommandHandler<SetPolicyCommand, bool>, SetPolicyHandler>();
        services.AddTransient<ICommandHandler<RegisterManifestCommand, ManifestResult>, RegisterManifestHandler>();
        services.AddTransient<ICommandHandler<CompleteManifestCommand, bool>, CompleteManifestHandler>();
        services.AddTransient<ICommandHandler<ResolvePlaybackCommand, PlaybackResult>, ResolvePlaybackHandler>();
        return services;
    }
}
