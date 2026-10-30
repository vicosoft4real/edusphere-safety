using System.Text.Json;
using EduSphere.Safety.Edge;

namespace EduSphere.Safety.EdgeAgent;

/// <summary>
/// Appends versioned event JSON (<c>edusphere-event/1</c>) — one object per line —
/// to the outbox file. Every event carries the rule version, the model version, and
/// the three timestamps (event, detection, ingest) separately.
/// </summary>
public sealed class OutboxWriter
{
    /// <summary>Outbox line format tag.</summary>
    public const string ExpectedFormat = "edusphere-event/1";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly object _gate = new();

    /// <summary>Creates a writer (parent directory is created).</summary>
    public OutboxWriter(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
    }

    /// <summary>
    /// Stable edge event id for cloud idempotency, derived from
    /// (camera, rule, track, event ticks) — the same event re-ingested after a
    /// restart yields the same id, so the spool rejects it as a duplicate.
    /// </summary>
    public static string EdgeEventId(string cameraId, RuleEvent e) =>
        $"{cameraId}/{e.RuleId}/{e.TrackId}/{e.EventTimeUtc.UtcTicks}";

    /// <summary>Appends one event; returns its edge event id.</summary>
    public string Append(string cameraId, RuleEvent e)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cameraId);
        ArgumentNullException.ThrowIfNull(e);
        var id = EdgeEventId(cameraId, e);
        var line = JsonSerializer.Serialize(new
        {
            format = ExpectedFormat,
            edgeEventId = id,
            cameraId,
            ruleId = e.RuleId,
            ruleVersion = e.RuleVersion,
            modelVersion = e.ModelVersion,
            trackId = e.TrackId,
            eventTimeUtc = e.EventTimeUtc,
            detectionTimeUtc = e.DetectionTimeUtc,
            ingestTimeUtc = e.IngestTimeUtc,
            footTrail = e.FootTrailSnapshot.Select(p => new { x = p.X, y = p.Y }).ToArray(),
        }, Json);
        lock (_gate)
        {
            File.AppendAllText(_path, line + "\n");
        }

        return id;
    }

    /// <summary>Appends one type-E health event; returns its edge event id.</summary>
    public string AppendHealth(string cameraId, HealthEvent e, DateTimeOffset ingestTimeUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cameraId);
        ArgumentNullException.ThrowIfNull(e);
        var id = $"{cameraId}/health/{e.Reason}/{e.EventTimeUtc.UtcTicks}";
        var line = JsonSerializer.Serialize(new
        {
            format = ExpectedFormat,
            type = e.Type,
            edgeEventId = id,
            cameraId,
            ruleId = e.RuleId,
            ruleVersion = e.RuleVersion,
            reason = e.Reason,
            eventTimeUtc = e.EventTimeUtc,
            ingestTimeUtc,
        }, Json);
        lock (_gate)
        {
            File.AppendAllText(_path, line + "\n");
        }

        return id;
    }

    /// <summary>Appends an ops note (e.g. spool-drop); never throws the pipeline.</summary>
    public void AppendOpsNote(string cameraId, string note, string detail)
    {
        var line = JsonSerializer.Serialize(new
        {
            format = "edusphere-ops-note/1",
            cameraId,
            note,
            detail,
        }, Json);
        lock (_gate)
        {
            File.AppendAllText(_path, line + "\n");
        }
    }
}
