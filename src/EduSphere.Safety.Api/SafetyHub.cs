using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace EduSphere.Safety.Api;

/// <summary>
/// Real-time fan-out for incident.created / incident.updated.
/// Group key is tenant + campus ($"{tid}:{campus}"); the campus claim is
/// optional, so every connection also joins its bare tenant group and
/// broadcasts go to both.
/// </summary>
public interface IIncidentBroadcaster
{
    Task IncidentCreatedAsync(string tenantId, string campusId, object payload, CancellationToken ct);

    Task IncidentUpdatedAsync(string tenantId, string campusId, object payload, CancellationToken ct);
}

[Authorize]
public sealed class SafetyHub : Hub
{
    public const string TenantGroupPrefix = "safety";

    public static string TenantGroup(string tenantId) => $"{TenantGroupPrefix}:{tenantId}";

    public static string CampusGroup(string tenantId, string campusId) => $"{TenantGroupPrefix}:{tenantId}:{campusId}";

    public override async Task OnConnectedAsync()
    {
        var tenantId = Context.User?.FindFirst("tid")?.Value;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroup(tenantId));
        var campusId = Context.User?.FindFirst("campus")?.Value;
        if (!string.IsNullOrWhiteSpace(campusId))
            await Groups.AddToGroupAsync(Context.ConnectionId, CampusGroup(tenantId, campusId));
        await base.OnConnectedAsync();
    }
}

public sealed class SignalRIncidentBroadcaster(IHubContext<SafetyHub> hub) : IIncidentBroadcaster
{
    private readonly IHubContext<SafetyHub> _hub = hub ?? throw new ArgumentNullException(nameof(hub));

    public Task IncidentCreatedAsync(string tenantId, string campusId, object payload, CancellationToken ct) =>
        SendAsync("incident.created", tenantId, campusId, payload, ct);

    public Task IncidentUpdatedAsync(string tenantId, string campusId, object payload, CancellationToken ct) =>
        SendAsync("incident.updated", tenantId, campusId, payload, ct);

    private Task SendAsync(string method, string tenantId, string campusId, object payload, CancellationToken ct)
    {
        var clients = _hub.Clients.Groups(SafetyHub.TenantGroup(tenantId), SafetyHub.CampusGroup(tenantId, campusId));
        return clients.SendAsync(method, payload, ct);
    }
}
