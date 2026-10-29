using EduSphere.Safety.Shared;

namespace EduSphere.Safety.Core;

public enum IncidentState { New, Acknowledged, Investigating, Resolved, Dismissed }

public sealed class Incident
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string TenantId { get; }
    public string CampusId { get; }
    public string EventType { get; }
    public string CameraId { get; }
    public IncidentState State { get; private set; } = IncidentState.New;
    public int Version { get; private set; }
    public string? EdgeEventId { get; }

    private Incident(string tenantId, string campusId, string eventType, string cameraId, string? edgeEventId)
    { TenantId = tenantId; CampusId = campusId; EventType = eventType; CameraId = cameraId; EdgeEventId = edgeEventId; }

    public static Incident Create(string tenantId, string campusId, string eventType, string cameraId, string? edgeEventId = null)
        => new(tenantId, campusId, eventType, cameraId, edgeEventId);

    /// <summary>
    /// Rehydrates an incident from persisted/snapshotted fields. Store- and test-only:
    /// every read must materialize a FRESH instance so each request owns an independent
    /// snapshot (single-owner-per-request); never hand out live shared references.
    /// </summary>
    public static Incident Rehydrate(
        Guid id,
        string tenantId,
        string campusId,
        string eventType,
        string cameraId,
        string? edgeEventId,
        IncidentState state,
        int version)
        => new(tenantId, campusId, eventType, cameraId, edgeEventId)
        {
            Id = id,
            State = state,
            Version = version,
        };

    private static readonly Dictionary<IncidentState, IncidentState[]> Allowed = new()
    {
        [IncidentState.New] = [IncidentState.Acknowledged],
        [IncidentState.Acknowledged] = [IncidentState.Investigating, IncidentState.Dismissed],
        [IncidentState.Investigating] = [IncidentState.Resolved, IncidentState.Dismissed],
        [IncidentState.Resolved] = [],
        [IncidentState.Dismissed] = [],
    };

    public Result<Incident> TransitionTo(IncidentState next, string actorId, string? reason = null)
    {
        if (!Allowed[State].Contains(next))
            return Result<Incident>.Fail("incident.invalid_transition", $"Cannot move from {State} to {next}.");
        if (next == IncidentState.Dismissed && string.IsNullOrWhiteSpace(reason))
            return Result<Incident>.Fail("incident.dismiss_reason_required", "Dismissal requires a reason.");
        State = next;
        Version++;
        return Result<Incident>.Ok(this);
    }
}
