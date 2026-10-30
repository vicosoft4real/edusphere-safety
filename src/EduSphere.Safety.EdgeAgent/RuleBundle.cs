using System.Text.Json;
using EduSphere.Safety.Edge;

namespace EduSphere.Safety.EdgeAgent;

/// <summary>Parsed rule bundle: version-checked rules plus schedules and model version.</summary>
public sealed record RuleBundle(
    int BundleVersion,
    string ModelVersion,
    IReadOnlyList<RuleConfig> Rules,
    IReadOnlyDictionary<string, Schedule> SchedulesById);

/// <summary>
/// Loads a versioned rule bundle file (<c>edusphere-rule-bundle/1</c>).
/// The format tag must match exactly and <c>bundleVersion</c> must be >= 1, or the
/// host refuses to start (stale-config detection against heartbeats is Plan 4).
/// <para>
/// STRICTNESS (every violation ⇒ <see cref="InvalidDataException"/>, i.e. refuse to
/// start, never a half-loaded rule set): ruleId non-empty, rule version >= 1,
/// threshold finite in 0..1, persistence >= 1, dwell/cooldown/exitMargin finite >= 0,
/// polygon >= 3 normalized vertices, direction in {-1, 0, 1} per rule type,
/// scheduleId/poolId must resolve (poolId against some schedule's pool sessions),
/// duplicate scheduleId (or ruleId) rejected. Malformed dates fall in here too —
/// date parsing throws <see cref="FormatException"/>, wrapped below. Unknown JSON
/// members are IGNORED (System.Text.Json default): forward-compatible bundles load,
/// and only the documented members above take effect.
/// </para>
/// </summary>
public static class RuleBundleLoader
{
    /// <summary>Expected bundle format tag.</summary>
    public const string ExpectedFormat = "edusphere-rule-bundle/1";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Loads and version-checks a bundle file.</summary>
    /// <exception cref="InvalidDataException">Bad format tag, version, shape, or values.</exception>
    public static RuleBundle LoadFromFile(string bundlePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        BundleDoc? doc;
        try
        {
            doc = JsonSerializer.Deserialize<BundleDoc>(File.ReadAllText(bundlePath), Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Rule bundle is not valid JSON.", ex);
        }

        try
        {
            if (doc is null || doc.Format != ExpectedFormat)
            {
                throw new InvalidDataException($"Rule bundle format must be {ExpectedFormat}.");
            }

            if (doc.BundleVersion < 1)
            {
                throw new InvalidDataException("Rule bundle version starts at 1.");
            }

            if (string.IsNullOrWhiteSpace(doc.ModelVersion))
            {
                throw new InvalidDataException("Rule bundle needs a model version.");
            }

            if (doc.Rules is null || doc.Rules.Count == 0)
            {
                throw new InvalidDataException("Rule bundle needs at least one rule.");
            }

            var schedules = new Dictionary<string, Schedule>(StringComparer.Ordinal);
            foreach (var schedule in doc.Schedules ?? [])
            {
                if (string.IsNullOrWhiteSpace(schedule.ScheduleId))
                {
                    throw new InvalidDataException("Schedule needs a scheduleId.");
                }

                if (!schedules.TryAdd(schedule.ScheduleId, ParseSchedule(schedule)))
                {
                    throw new InvalidDataException($"Duplicate scheduleId: {schedule.ScheduleId}.");
                }
            }

            var rules = doc.Rules.Select(ParseRule).ToArray();
            var ruleIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rule in rules)
            {
                if (!ruleIds.Add(rule.RuleId))
                {
                    throw new InvalidDataException($"Duplicate ruleId: {rule.RuleId}.");
                }

                if (rule.ScheduleId is not null && !schedules.ContainsKey(rule.ScheduleId))
                {
                    throw new InvalidDataException(
                        $"Rule {rule.RuleId} names unknown scheduleId: {rule.ScheduleId}.");
                }

                if (rule.PoolId is not null && !PoolResolves(rule.PoolId, schedules))
                {
                    throw new InvalidDataException(
                        $"Rule {rule.RuleId} names unknown poolId: {rule.PoolId}.");
                }
            }

            return new RuleBundle(doc.BundleVersion, doc.ModelVersion, rules, schedules);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException($"Rule bundle has a malformed date/time value ({ex.Message}).", ex);
        }
        catch (ArgumentException ex)
        {
            // RuleConfig / PolygonZone / TrackedPoint / Schedule / PoolSession range
            // checks (threshold, polygon vertices, direction, …): bundle data, not code.
            throw new InvalidDataException($"Rule bundle has an invalid value ({ex.Message}).", ex);
        }
    }

    private static Schedule ParseSchedule(ScheduleDoc schedule) =>
        new(
            schedule.IanaTimezone,
            schedule.ClosedWindows?.Select(w => new DailyWindow(
                TimeOnly.Parse(w.Start, System.Globalization.CultureInfo.InvariantCulture),
                TimeOnly.Parse(w.End, System.Globalization.CultureInfo.InvariantCulture))).ToArray()
                ?? [],
            schedule.Holidays?.Select(d => DateOnly.Parse(d, System.Globalization.CultureInfo.InvariantCulture)).ToArray(),
            schedule.Exceptions?.ToDictionary(
                kv => DateOnly.Parse(kv.Key, System.Globalization.CultureInfo.InvariantCulture),
                kv => kv.Value),
            schedule.PoolSessions?.Select(p => new PoolSession(
                p.PoolId,
                DateTimeOffset.Parse(p.StartUtc, System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(p.EndUtc, System.Globalization.CultureInfo.InvariantCulture))).ToArray());

    private static bool PoolResolves(string poolId, Dictionary<string, Schedule> schedules) =>
        schedules.Values.Any(s =>
            s.PoolSessions is not null &&
            s.PoolSessions.Any(session => session.PoolId == poolId));

    private static RuleConfig ParseRule(RuleDoc doc)
    {
        if (!Enum.TryParse<RuleType>(doc.Type, ignoreCase: false, out var type))
        {
            throw new InvalidDataException($"Unknown rule type: {doc.Type}.");
        }

        PolygonZone? polygon = null;
        if (doc.Polygon is not null)
        {
            polygon = new PolygonZone(doc.Polygon.Select((p, i) =>
            {
                if (p is null || p.Length < 2)
                {
                    throw new InvalidDataException(
                        $"Rule {doc.RuleId} polygon vertex {i} needs [x, y].");
                }

                return new TrackedPoint(p[0], p[1]);
            }));
        }

        DirectedBoundary? boundary = null;
        if (doc.Boundary is not null)
        {
            boundary = new DirectedBoundary(
                new TrackedPoint(doc.Boundary.Ax, doc.Boundary.Ay),
                new TrackedPoint(doc.Boundary.Bx, doc.Boundary.By));
        }

        DateTimeOffset? pausedUntil = doc.PausedUntilUtc is null
            ? null
            : DateTimeOffset.Parse(doc.PausedUntilUtc, System.Globalization.CultureInfo.InvariantCulture);

        return new RuleConfig(
            doc.RuleId, type, doc.Version,
            doc.ConfidenceThreshold ?? 0.5,
            doc.PersistenceFrames ?? 3,
            doc.DwellSeconds ?? 0.0,
            doc.CooldownSeconds ?? 0.0,
            doc.ExitMargin ?? 0.0,
            polygon, boundary, doc.Direction ?? 0,
            doc.ScheduleId, doc.PoolId, pausedUntil);
    }

    private sealed class BundleDoc
    {
        public string? Format { get; set; }
        public int BundleVersion { get; set; }
        public string? ModelVersion { get; set; }
        public List<ScheduleDoc>? Schedules { get; set; }
        public List<RuleDoc>? Rules { get; set; }
    }

    private sealed class ScheduleDoc
    {
        public string? ScheduleId { get; set; }
        public string IanaTimezone { get; set; } = "UTC";
        public List<WindowDoc>? ClosedWindows { get; set; }
        public List<string>? Holidays { get; set; }
        public Dictionary<string, bool>? Exceptions { get; set; }
        public List<PoolDoc>? PoolSessions { get; set; }
    }

    private sealed class WindowDoc
    {
        public string Start { get; set; } = "00:00";
        public string End { get; set; } = "00:00";
    }

    private sealed class PoolDoc
    {
        public string PoolId { get; set; } = string.Empty;
        public string StartUtc { get; set; } = string.Empty;
        public string EndUtc { get; set; } = string.Empty;
    }

    private sealed class RuleDoc
    {
        public string RuleId { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public int Version { get; set; }
        public double? ConfidenceThreshold { get; set; }
        public int? PersistenceFrames { get; set; }
        public double? DwellSeconds { get; set; }
        public double? CooldownSeconds { get; set; }
        public double? ExitMargin { get; set; }
        public List<double[]>? Polygon { get; set; }
        public BoundaryDoc? Boundary { get; set; }
        public int? Direction { get; set; }
        public string? ScheduleId { get; set; }
        public string? PoolId { get; set; }
        public string? PausedUntilUtc { get; set; }
    }

    private sealed class BoundaryDoc
    {
        public double Ax { get; set; }
        public double Ay { get; set; }
        public double Bx { get; set; }
        public double By { get; set; }
    }
}
