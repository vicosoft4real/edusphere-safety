namespace EduSphere.Safety.Edge;

/// <summary>
/// Rule kinds. Deterministic evaluation over track histories, never single frames:
/// <list type="bullet">
/// <item>A <see cref="ZoneIntrusion"/>: foot point inside the polygon for at least
/// <c>PersistenceFrames</c> consecutive frames while the schedule says closed.</item>
/// <item>B <see cref="BoundaryCrossing"/>: sustained directed crossing per
/// <see cref="DirectedBoundary.CrossedSide"/> matching the expected direction.</item>
/// <item>C <see cref="Dwell"/>: continuous presence for at least <c>DwellSeconds</c>,
/// measured on EVENT timestamps; exit or track grace-expiry restarts the clock.</item>
/// <item>D <see cref="PoolZone"/>: like A against the pool polygon, silent while an
/// authorised <see cref="PoolSession"/> covers the event time.</item>
/// </list>
/// (Type E health events are produced by the ingest layer, not this engine; it exposes
/// <see cref="RuleEngine.NoteHealthyFrame"/> + <see cref="RuleEngine.HealthyFrameAgeAt"/>
/// for that layer.)
/// </summary>
public enum RuleType
{
    /// <summary>Type A: zone intrusion.</summary>
    ZoneIntrusion = 0,

    /// <summary>Type B: directed boundary crossing.</summary>
    BoundaryCrossing = 1,

    /// <summary>Type C: dwell/loitering.</summary>
    Dwell = 2,

    /// <summary>Type D: pool zone with session authorisation.</summary>
    PoolZone = 3,
}

/// <summary>
/// Versioned rule configuration. <c>Version</c> starts at 1; a version change during an
/// active episode discards partial state — see <see cref="RuleEngine.UpdateRules"/>.
/// </summary>
public sealed class RuleConfig
{
    /// <summary>Creates a rule; per-type required geometry is enforced here.</summary>
    public RuleConfig(
        string ruleId,
        RuleType type,
        int version,
        double confidenceThreshold = 0.5,
        int persistenceFrames = 3,
        double dwellSeconds = 0.0,
        double cooldownSeconds = 0.0,
        double exitMargin = 0.0,
        PolygonZone? polygon = null,
        DirectedBoundary? boundary = null,
        int direction = 0,
        string? scheduleId = null,
        string? poolId = null,
        DateTimeOffset? pausedUntilUtc = null)
    {
        if (string.IsNullOrWhiteSpace(ruleId))
        {
            throw new ArgumentException("Rule id must not be empty.", nameof(ruleId));
        }

        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Rule version starts at 1.");
        }

        if (double.IsNaN(confidenceThreshold) || double.IsInfinity(confidenceThreshold) ||
            confidenceThreshold < 0.0 || confidenceThreshold > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(confidenceThreshold), "Confidence threshold must be in 0..1.");
        }

        if (persistenceFrames < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(persistenceFrames), "Persistence frames must be at least 1.");
        }

        if (double.IsNaN(dwellSeconds) || double.IsInfinity(dwellSeconds) || dwellSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(dwellSeconds), "Dwell seconds must be finite and >= 0.");
        }

        if (double.IsNaN(cooldownSeconds) || double.IsInfinity(cooldownSeconds) || cooldownSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(cooldownSeconds), "Cooldown seconds must be finite and >= 0.");
        }

        if (double.IsNaN(exitMargin) || double.IsInfinity(exitMargin) || exitMargin < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(exitMargin), "Exit margin must be finite and >= 0.");
        }

        switch (type)
        {
            case RuleType.ZoneIntrusion:
            case RuleType.Dwell:
            case RuleType.PoolZone:
                if (polygon is null)
                {
                    throw new ArgumentException($"Rule type {type} requires a polygon zone.", nameof(polygon));
                }

                if (boundary is not null)
                {
                    throw new ArgumentException($"Rule type {type} takes no boundary.", nameof(boundary));
                }

                if (direction != 0)
                {
                    throw new ArgumentException($"Rule type {type} takes no crossing direction.", nameof(direction));
                }

                break;
            case RuleType.BoundaryCrossing:
                if (boundary is null)
                {
                    throw new ArgumentException("Boundary-crossing rules require a boundary.", nameof(boundary));
                }

                if (polygon is not null)
                {
                    throw new ArgumentException("Boundary-crossing rules take no polygon zone.", nameof(polygon));
                }

                if (direction != 1 && direction != -1)
                {
                    throw new ArgumentOutOfRangeException(nameof(direction), "Crossing direction must be +1 or -1.");
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), "Unknown rule type.");
        }

        if (type == RuleType.Dwell && dwellSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(dwellSeconds), "Dwell rules need a positive dwell duration.");
        }

        RuleId = ruleId;
        Type = type;
        Version = version;
        ConfidenceThreshold = confidenceThreshold;
        PersistenceFrames = persistenceFrames;
        DwellSeconds = dwellSeconds;
        CooldownSeconds = cooldownSeconds;
        ExitMargin = exitMargin;
        Polygon = polygon;
        Boundary = boundary;
        Direction = direction;
        ScheduleId = scheduleId;
        PoolId = poolId;
        PausedUntilUtc = pausedUntilUtc;
    }

    /// <summary>Stable rule identifier.</summary>
    public string RuleId { get; }

    /// <summary>Rule kind (A/B/C/D).</summary>
    public RuleType Type { get; }

    /// <summary>Config version; starts at 1.</summary>
    public int Version { get; }

    /// <summary>
    /// Minimum latest-detection confidence. Detections below threshold never reach the
    /// engine — filtering them is the detector's job; the engine re-checks defensively
    /// and treats below-threshold frames as absent (they break consecutiveness).
    /// </summary>
    public double ConfidenceThreshold { get; }

    /// <summary>Consecutive inside frames required (types A and D).</summary>
    public int PersistenceFrames { get; }

    /// <summary>Continuous presence required, in seconds on event timestamps (type C).</summary>
    public double DwellSeconds { get; }

    /// <summary>Per (rule, track) quiet period after firing, in seconds.</summary>
    public double CooldownSeconds { get; }

    /// <summary>
    /// Outer hysteresis band (normalized units) for the exit side: presence counts via
    /// <c>Contains</c>, but an episode ends only outside
    /// <c>ContainsWithMargin(point, ExitMargin)</c>.
    /// </summary>
    public double ExitMargin { get; }

    /// <summary>Zone polygon (types A, C, D).</summary>
    public PolygonZone? Polygon { get; }

    /// <summary>Directed segment (type B).</summary>
    public DirectedBoundary? Boundary { get; }

    /// <summary>Expected <see cref="DirectedBoundary.CrossedSide"/> sign (type B).</summary>
    public int Direction { get; }

    /// <summary>Schedule gate; null means always armed.</summary>
    public string? ScheduleId { get; }

    /// <summary>Pool authorising sessions are matched against (type D; null = any pool).</summary>
    public string? PoolId { get; }

    /// <summary>
    /// Temporary pause: frames with event time before this instant neither accumulate
    /// state nor fire (in-progress state is discarded while paused).
    /// </summary>
    public DateTimeOffset? PausedUntilUtc { get; }
}

/// <summary>
/// A track plus the detector signal the engine gates on. Below-threshold detections
/// never reach the engine in production (the detector filters them); the engine still
/// re-checks <see cref="Confidence"/> against the rule threshold defensively.
/// Task 5 builds this from the track itself:
/// <c>new ScoredTrack(track, track.Confidence, track.LastDetectionTimeUtc)</c> —
/// the per-frame wrapper exists so evaluation stays a pure function of its inputs.
/// </summary>
public sealed class ScoredTrack
{
    /// <summary>Creates a scored track.</summary>
    public ScoredTrack(Track track, double confidence, DateTimeOffset detectionTimeUtc)
    {
        Track = track ?? throw new ArgumentNullException(nameof(track));
        if (double.IsNaN(confidence) || double.IsInfinity(confidence) || confidence < 0.0 || confidence > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence must be in 0..1.");
        }

        Confidence = confidence;
        DetectionTimeUtc = detectionTimeUtc;
    }

    /// <summary>The tracked person.</summary>
    public Track Track { get; }

    /// <summary>Latest-detection confidence for the gate.</summary>
    public double Confidence { get; }

    /// <summary>When the detector produced this detection (kept separate from frame/event time).</summary>
    public DateTimeOffset DetectionTimeUtc { get; }
}

/// <summary>
/// One fired rule event. <see cref="EventTimeUtc"/> is the frame (event) time;
/// <see cref="DetectionTimeUtc"/> is the detector output time; <see cref="IngestTimeUtc"/>
/// defaults to the event time and is SET BY THE CALLER layer (uploader/outbox) when the
/// event is ingested — the engine never stamps it. <see cref="SupersedesEventId"/> is
/// null unless the caller supplies the superseded id when re-emitting under a new rule
/// version.
/// </summary>
public sealed class RuleEvent
{
    /// <summary>Creates an event; ingest time and supersedes link are caller-settable after.</summary>
    public RuleEvent(
        string ruleId,
        int ruleVersion,
        long trackId,
        DateTimeOffset eventTimeUtc,
        DateTimeOffset detectionTimeUtc,
        IReadOnlyList<TrackedPoint> footTrailSnapshot,
        string modelVersion)
    {
        if (string.IsNullOrWhiteSpace(ruleId))
        {
            throw new ArgumentException("Rule id must not be empty.", nameof(ruleId));
        }

        if (string.IsNullOrWhiteSpace(modelVersion))
        {
            throw new ArgumentException("Model version must not be empty.", nameof(modelVersion));
        }

        RuleId = ruleId;
        RuleVersion = ruleVersion;
        TrackId = trackId;
        EventTimeUtc = eventTimeUtc;
        DetectionTimeUtc = detectionTimeUtc;
        FootTrailSnapshot = footTrailSnapshot ?? throw new ArgumentNullException(nameof(footTrailSnapshot));
        ModelVersion = modelVersion;
        IngestTimeUtc = eventTimeUtc;
    }

    /// <summary>Firing rule id.</summary>
    public string RuleId { get; }

    /// <summary>Firing rule version.</summary>
    public int RuleVersion { get; }

    /// <summary>Firing track id.</summary>
    public long TrackId { get; }

    /// <summary>Frame (event) time. Evaluation ALWAYS uses this, never wall-clock.</summary>
    public DateTimeOffset EventTimeUtc { get; }

    /// <summary>Detector output time.</summary>
    public DateTimeOffset DetectionTimeUtc { get; }

    /// <summary>Set by the caller/ingest layer; defaults to <see cref="EventTimeUtc"/>.</summary>
    public DateTimeOffset IngestTimeUtc { get; set; }

    /// <summary>Foot-point trail snapshot at fire time, oldest first.</summary>
    public IReadOnlyList<TrackedPoint> FootTrailSnapshot { get; }

    /// <summary>Detector model version passed into evaluation.</summary>
    public string ModelVersion { get; }

    /// <summary>
    /// Links the superseded event when re-emitted under a new rule version; null unless
    /// the caller supplies it.
    /// </summary>
    public string? SupersedesEventId { get; set; }
}

/// <summary>
/// Deterministic rule evaluation over track histories. Pure apart from held episode
/// state: every timestamp is passed in, no clock is read. Episodes fire at most once
/// while their condition holds (re-arm requires the condition to lapse); a per
/// (rule, track) cooldown additionally suppresses refires after the episode ends.
/// Tracks absent from an evaluation lose their episode state (tracker grace-expiry
/// resets presence/dwell); cooldown markers survive.
/// Pinned semantics (Task 3 quality review — normative):
/// <list type="bullet">
/// <item>Presence follows the TRACKER: a track in <c>ActiveTracks</c> counts as present
/// even on grace-held stale boxes (occlusion bridges dwell/persistence); a track
/// absent from <c>ActiveTracks</c> drops its episode (reset). Task 5 MUST pass
/// <c>ActiveTracks</c> through unfiltered — the tracker output IS the presence signal,
/// including tracks on grace (<see cref="Track.Misses"/> &gt; 0) whose boxes have not
/// been re-observed for several frames.</item>
/// <item>Below-threshold track: skipped AS IF ABSENT — the episode is dropped, breaking
/// consecutiveness exactly like a tracker absence. Threshold jitter can therefore
/// reset episodes; detector debounce is the documented mitigation, not engine logic.</item>
/// <item>Boundary re-arm: an opposite-direction crossing CLEARS the fired latch (re-arm);
/// a refire still needs a fresh expected-direction crossing AND an elapsed cooldown.
/// Re-arm requires the crossing itself — a no-crossing frame holds the latch.</item>
/// <item>Schedule-open frames FREEZE episode state: no <c>InsideCount</c> /
/// <c>PresenceStart</c> change, no fire, no lapse. Evaluation resumes on close; frames
/// observed while open never count, so there is no stale fire on the first closed frame.</item>
/// <item>Multi-rule same-frame fire order: sorted by rule id (deterministic).</item>
/// </list>
/// </summary>
public sealed class RuleEngine
{
    private sealed class Episode
    {
        public int InsideCount;
        public DateTimeOffset? PresenceStart;
        public bool Fired;
    }

    private readonly Dictionary<string, RuleConfig> _rules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _knownVersions = new(StringComparer.Ordinal);
    private readonly Dictionary<(string RuleId, long TrackId), Episode> _episodes = new();
    private readonly Dictionary<(string RuleId, long TrackId), DateTimeOffset> _lastFired = new();
    private readonly ScheduleEvaluator _evaluator;
    private readonly IReadOnlyDictionary<string, Schedule> _schedulesById;
    private DateTimeOffset? _lastHealthyFrameTime;

    /// <summary>Creates an engine over <paramref name="rules"/> (ids must be unique).</summary>
    public RuleEngine(
        IEnumerable<RuleConfig> rules,
        ScheduleEvaluator? evaluator = null,
        IReadOnlyDictionary<string, Schedule>? schedulesById = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _evaluator = evaluator ?? new ScheduleEvaluator();
        _schedulesById = schedulesById ?? new Dictionary<string, Schedule>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            ArgumentNullException.ThrowIfNull(rule);
            if (!_rules.TryAdd(rule.RuleId, rule))
            {
                throw new ArgumentException($"Duplicate rule id: {rule.RuleId}.", nameof(rules));
            }

            _knownVersions[rule.RuleId] = rule.Version;
        }
    }

    /// <summary>
    /// Replaces the rule set (config pull path). Partial episode state for rules whose
    /// version changed — or which were removed — is discarded so the new version
    /// re-evaluates from scratch; unchanged rules keep their in-progress state.
    /// Cooldown markers survive. Call <see cref="Reset"/> to discard everything.
    /// </summary>
    public void UpdateRules(IEnumerable<RuleConfig> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var next = new Dictionary<string, RuleConfig>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            ArgumentNullException.ThrowIfNull(rule);
            if (!next.TryAdd(rule.RuleId, rule))
            {
                throw new ArgumentException($"Duplicate rule id: {rule.RuleId}.", nameof(rules));
            }
        }

        foreach (var id in _rules.Keys)
        {
            if (!next.TryGetValue(id, out var replacement) || replacement.Version != _knownVersions[id])
            {
                foreach (var key in _episodes.Keys.Where(k => k.RuleId == id).ToArray())
                {
                    _episodes.Remove(key);
                }
            }
        }

        _rules.Clear();
        _knownVersions.Clear();
        foreach (var rule in next.Values)
        {
            _rules[rule.RuleId] = rule;
            _knownVersions[rule.RuleId] = rule.Version;
        }
    }

    /// <summary>Discards in-progress episode state (rule-version change path). Cooldown markers survive.</summary>
    public void Reset() => _episodes.Clear();

    /// <summary>Records the latest healthy frame event time (ingest layer calls this).</summary>
    public void NoteHealthyFrame(DateTimeOffset eventTimeUtc) => _lastHealthyFrameTime = eventTimeUtc;

    /// <summary>
    /// Age of the last healthy frame at <paramref name="queryTimeUtc"/>, or null when no
    /// healthy frame has been noted. The ingest layer turns a stale age into a type-E
    /// health event; the engine itself never emits E-events.
    /// </summary>
    public TimeSpan? HealthyFrameAgeAt(DateTimeOffset queryTimeUtc) =>
        _lastHealthyFrameTime is null ? null : queryTimeUtc - _lastHealthyFrameTime;

    /// <summary>
    /// Evaluates all rules over <paramref name="tracks"/> at <paramref name="frameTimeUtc"/>
    /// (the frame/event time) and returns the events fired by this evaluation, ordered
    /// by rule id (deterministic multi-rule fire order).
    /// Presence follows the TRACKER: pass the tracker's <c>ActiveTracks</c> through
    /// UNFILTERED — a held track counts as present even while its box is a grace-held
    /// stale box (occlusion bridges dwell/persistence). A track missing from
    /// <paramref name="tracks"/> drops its episode; a below-threshold track is skipped
    /// as if absent for the same reason. Schedule-open frames freeze episode state
    /// (no accumulate, no lapse, no fire); a paused rule discards in-progress state.
    /// CALLER CONTRACT: <paramref name="frameTimeUtc"/> MUST be non-decreasing across
    /// successive calls (event time, never wall-clock). Dwell arithmetic and cooldown
    /// windows assume forward time; out-of-order times produce undefined
    /// episode/cooldown results. The engine does not guard this — ordering is the
    /// caller's job (the pipeline feeds frames in capture order).
    /// </summary>
    public IReadOnlyList<RuleEvent> Evaluate(
        DateTimeOffset frameTimeUtc,
        IReadOnlyList<ScoredTrack> tracks,
        string modelVersion,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (string.IsNullOrWhiteSpace(modelVersion))
        {
            throw new ArgumentException("Model version must not be empty.", nameof(modelVersion));
        }

        var fired = new List<RuleEvent>();
        var seen = new HashSet<(string RuleId, long TrackId)>();
        var ordered = _rules.Values.OrderBy(static rule => rule.RuleId, StringComparer.Ordinal).ToArray();
        foreach (var rule in ordered)
        {
            ct.ThrowIfCancellationRequested();
            var closed = IsClosedAt(rule, frameTimeUtc);
            var paused = rule.PausedUntilUtc is not null && frameTimeUtc < rule.PausedUntilUtc;
            foreach (var scored in tracks)
            {
                ct.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(scored);
                var key = (rule.RuleId, scored.Track.Id);
                seen.Add(key);
                if (paused)
                {
                    _episodes.Remove(key);
                    continue;
                }

                if (!closed)
                {
                    // Schedule-open frame: FREEZE — hold the episode untouched (no
                    // InsideCount/PresenceStart change, no fire, no lapse). The key is
                    // already in `seen`, so the absent-track sweep below keeps the
                    // frozen episode instead of dropping it.
                    continue;
                }

                EvaluateOne(rule, key, scored, frameTimeUtc, modelVersion, fired);
            }
        }

        foreach (var key in _episodes.Keys.ToArray())
        {
            if (!seen.Contains(key))
            {
                _episodes.Remove(key);
            }
        }

        return fired;
    }

    private void EvaluateOne(
        RuleConfig rule,
        (string RuleId, long TrackId) key,
        ScoredTrack scored,
        DateTimeOffset frameTimeUtc,
        string modelVersion,
        List<RuleEvent> fired)
    {
        var trail = scored.Track.FootPointTrail;
        if (trail.Count == 0 || scored.Confidence < rule.ConfidenceThreshold)
        {
            // Below-threshold (or trail-less) track: skip AS IF ABSENT — the episode is
            // dropped, breaking consecutiveness exactly like a tracker absence.
            // Threshold jitter can therefore reset episodes; detector debounce is the
            // documented mitigation, not engine logic.
            _episodes.Remove(key);
            return;
        }

        var foot = trail[trail.Count - 1];
        switch (rule.Type)
        {
            case RuleType.ZoneIntrusion:
            case RuleType.PoolZone:
                EvaluatePresenceRule(rule, key, scored, foot, frameTimeUtc, modelVersion, fired);
                break;
            case RuleType.BoundaryCrossing:
                EvaluateBoundaryRule(rule, key, scored, frameTimeUtc, modelVersion, fired);
                break;
            case RuleType.Dwell:
                EvaluateDwellRule(rule, key, scored, foot, frameTimeUtc, modelVersion, fired);
                break;
            default:
                throw new InvalidOperationException($"Unknown rule type: {rule.Type}.");
        }
    }

    private void EvaluatePresenceRule(
        RuleConfig rule,
        (string RuleId, long TrackId) key,
        ScoredTrack scored,
        TrackedPoint foot,
        DateTimeOffset frameTimeUtc,
        string modelVersion,
        List<RuleEvent> fired)
    {
        var polygon = rule.Polygon!;
        var inside = polygon.Contains(foot);
        var episode = GetEpisode(key);
        if (inside)
        {
            episode.InsideCount++;
        }
        else if (!polygon.ContainsWithMargin(foot, rule.ExitMargin))
        {
            // True exit (outside the hysteresis band): the episode ends and the rule
            // re-arms. Frames merely inside the margin hold the count.
            episode.InsideCount = 0;
            episode.Fired = false;
        }

        var sessionSilent = rule.Type == RuleType.PoolZone && IsSessionAuthorised(rule, frameTimeUtc);
        if (inside &&
            episode.InsideCount >= rule.PersistenceFrames &&
            !episode.Fired &&
            !sessionSilent &&
            CooldownElapsed(key, rule, frameTimeUtc))
        {
            Fire(key, rule, scored, frameTimeUtc, modelVersion, fired);
            episode.Fired = true;
        }
    }

    private void EvaluateBoundaryRule(
        RuleConfig rule,
        (string RuleId, long TrackId) key,
        ScoredTrack scored,
        DateTimeOffset frameTimeUtc,
        string modelVersion,
        List<RuleEvent> fired)
    {
        // CrossedSide already requires a crossing sustained over two consecutive frame
        // steps, so the first matching evaluation fires (episode-latched + cooldown).
        var side = rule.Boundary!.CrossedSide(scored.Track.FootPointTrail);
        var episode = GetEpisode(key);
        if (side == 0)
        {
            // No sustained crossing on this frame: hold ALL episode state, fired latch
            // included. Re-arm requires an actual opposite-direction crossing below —
            // a gap frame must not disarm the rule by itself.
            return;
        }

        if (side != rule.Direction)
        {
            // Opposite-direction crossing clears the fired latch (re-arm). A refire
            // still needs a fresh expected-direction crossing AND an elapsed cooldown.
            episode.Fired = false;
            return;
        }

        if (!episode.Fired && CooldownElapsed(key, rule, frameTimeUtc))
        {
            Fire(key, rule, scored, frameTimeUtc, modelVersion, fired);
            episode.Fired = true;
        }
    }

    private void EvaluateDwellRule(
        RuleConfig rule,
        (string RuleId, long TrackId) key,
        ScoredTrack scored,
        TrackedPoint foot,
        DateTimeOffset frameTimeUtc,
        string modelVersion,
        List<RuleEvent> fired)
    {
        var polygon = rule.Polygon!;
        var inside = polygon.Contains(foot);
        var episode = GetEpisode(key);
        if (inside)
        {
            episode.PresenceStart ??= frameTimeUtc;
            var dwell = TimeSpan.FromSeconds(rule.DwellSeconds);
            if (!episode.Fired &&
                frameTimeUtc - episode.PresenceStart.Value >= dwell &&
                CooldownElapsed(key, rule, frameTimeUtc))
            {
                Fire(key, rule, scored, frameTimeUtc, modelVersion, fired);
                episode.Fired = true;
            }
        }
        else if (!polygon.ContainsWithMargin(foot, rule.ExitMargin))
        {
            // Exit outside the hysteresis band restarts the dwell clock from scratch.
            episode.PresenceStart = null;
            episode.Fired = false;
        }
    }

    private void Fire(
        (string RuleId, long TrackId) key,
        RuleConfig rule,
        ScoredTrack scored,
        DateTimeOffset frameTimeUtc,
        string modelVersion,
        List<RuleEvent> fired)
    {
        fired.Add(new RuleEvent(
            rule.RuleId,
            rule.Version,
            scored.Track.Id,
            frameTimeUtc,
            scored.DetectionTimeUtc,
            scored.Track.FootPointTrail.ToArray(),
            modelVersion));
        _lastFired[key] = frameTimeUtc;
    }

    private Episode GetEpisode((string RuleId, long TrackId) key)
    {
        if (!_episodes.TryGetValue(key, out var episode))
        {
            episode = new Episode();
            _episodes[key] = episode;
        }

        return episode;
    }

    private bool CooldownElapsed((string RuleId, long TrackId) key, RuleConfig rule, DateTimeOffset frameTimeUtc) =>
        !_lastFired.TryGetValue(key, out var last) ||
        (frameTimeUtc - last).TotalSeconds >= rule.CooldownSeconds;

    private bool IsClosedAt(RuleConfig rule, DateTimeOffset frameTimeUtc)
    {
        if (rule.ScheduleId is null)
        {
            return true;
        }

        if (!_schedulesById.TryGetValue(rule.ScheduleId, out var schedule))
        {
            throw new InvalidOperationException($"Unknown schedule id on rule {rule.RuleId}: {rule.ScheduleId}.");
        }

        return _evaluator.IsClosedAt(frameTimeUtc, schedule);
    }

    private bool IsSessionAuthorised(RuleConfig rule, DateTimeOffset frameTimeUtc)
    {
        if (rule.ScheduleId is null)
        {
            return false;
        }

        if (!_schedulesById.TryGetValue(rule.ScheduleId, out var schedule) || schedule.PoolSessions is null)
        {
            return false;
        }

        return schedule.PoolSessions.Any(session =>
            (rule.PoolId is null || session.PoolId == rule.PoolId) && session.Covers(frameTimeUtc));
    }
}
