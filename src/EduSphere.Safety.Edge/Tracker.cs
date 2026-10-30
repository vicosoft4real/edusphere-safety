using System.Diagnostics;

namespace EduSphere.Safety.Edge;

/// <summary>
/// One tracked person: a stable identity carried across frames by greedy IoU matching.
/// Carries the last-matched detection signal (<see cref="Confidence"/> +
/// <see cref="LastDetectionTimeUtc"/>), set on construction and refreshed on every
/// <see cref="Match"/>: Task 5 builds <c>ScoredTrack</c> from these two fields, so the
/// engine gates on the track itself instead of a side-channel wrapper.
/// <see cref="Confidence"/> is a detector output signal used for thresholding only —
/// it is NOT a danger probability and must never be presented as one.
/// </summary>
public sealed class Track
{
    private readonly Queue<TrackedPoint> _footPointTrail = new();

    /// <summary>Creates a track. Callers should use <see cref="Tracker.Update"/> instead.</summary>
    public Track(long id, BoundingBox box, long lastSeenFrame, double confidence, DateTimeOffset lastDetectionTimeUtc)
    {
        if (id < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "Track id must be positive.");
        }

        Id = id;
        Box = box ?? throw new ArgumentNullException(nameof(box));
        if (lastSeenFrame < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lastSeenFrame), "Frame number must be non-negative.");
        }

        LastSeenFrame = lastSeenFrame;
        Confidence = ValidateConfidence(confidence);
        LastDetectionTimeUtc = lastDetectionTimeUtc;
        AppendFootPoint(box);
    }

    /// <summary>Stable identity, allocated monotonically from 1 and never reused within a run.</summary>
    public long Id { get; }

    /// <summary>Latest associated detection box.</summary>
    public BoundingBox Box { get; private set; }

    /// <summary>
    /// Confidence of the most recently matched detection (detector signal for
    /// thresholding only, NOT a danger probability).
    /// </summary>
    public double Confidence { get; private set; }

    /// <summary>
    /// Event time of the frame that produced the most recently matched detection.
    /// Detections in one frame share that frame's event time (see
    /// <see cref="Tracker.Update"/>); per-detection times are not tracked separately.
    /// </summary>
    public DateTimeOffset LastDetectionTimeUtc { get; private set; }

    /// <summary>
    /// Foot-point history (bottom-center of each associated box), oldest first,
    /// bounded to the last <see cref="Tracker.MaxTrailLength"/> points.
    /// </summary>
    public IReadOnlyList<TrackedPoint> FootPointTrail => _footPointTrail.ToArray();

    /// <summary>Alias for <see cref="FootPointTrail"/>.</summary>
    public IReadOnlyList<TrackedPoint> Trail => FootPointTrail;

    /// <summary>Consecutive frames without a match; reset to 0 on every match.</summary>
    public int Misses { get; private set; }

    /// <summary>Frame number of the most recent match.</summary>
    public long LastSeenFrame { get; private set; }

    internal void Match(BoundingBox box, long frameNumber, double confidence, DateTimeOffset detectionTimeUtc)
    {
        Box = box;
        LastSeenFrame = frameNumber;
        Confidence = ValidateConfidence(confidence);
        LastDetectionTimeUtc = detectionTimeUtc;
        Misses = 0;
        AppendFootPoint(box);
    }

    internal void Miss() => Misses++;

    private static double ValidateConfidence(double confidence)
    {
        // Same 1e-9 slack as Detection: a detector output at the exact boundary must
        // survive float rounding (Detection accepts up to 1.0 + 1e-9, and Update feeds
        // Detection.Confidence straight through here).
        if (double.IsNaN(confidence) || double.IsInfinity(confidence) || confidence < -1e-9 || confidence > 1.0 + 1e-9)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence must be a finite value in 0..1 (within 1e-9 rounding slack).");
        }

        return confidence;
    }

    private void AppendFootPoint(BoundingBox box)
    {
        _footPointTrail.Enqueue(new TrackedPoint(box.X + box.W / 2.0, box.Y + box.H));
        while (_footPointTrail.Count > Tracker.MaxTrailLength)
        {
            _footPointTrail.Dequeue();
        }
    }
}

/// <summary>The tracker output for one frame: the frame number plus the active tracks only.</summary>
/// <param name="Frame">Source-assigned frame sequence number this output belongs to.</param>
/// <param name="ActiveTracks">Live tracks after this update, ordered by id. Never null.</param>
public sealed record TrackedFrame(long Frame, IReadOnlyList<Track> ActiveTracks);

/// <summary>
/// ByteTrack-lite person tracker: greedy IoU matching with no motion model.
/// No Kalman filter by design: frame-to-frame association is deterministic and CPU-cheap,
/// which is sufficient for zone/dwell semantics evaluated on foot-point trails.
/// </summary>
public sealed class Tracker
{
    /// <summary>
    /// Minimum intersection-over-union for a detection to associate with a track.
    /// Pairs below this never match, however isolated the track is; pairs are matched
    /// greedily in descending IoU order with each track and detection used at most once.
    /// </summary>
    public const double IouThreshold = 0.3;

    /// <summary>Maximum foot points retained per track trail (oldest dropped first).</summary>
    public const int MaxTrailLength = 32;

    private const int DefaultGraceFrames = 5;

    private readonly List<Track> _activeTracks = new();
    private long _nextId;

    /// <summary>Creates a tracker.</summary>
    /// <param name="graceFrames">
    /// Consecutive unmatched frames a track survives before retiring. Must be >= 1:
    /// a track missing for exactly <paramref name="graceFrames"/> frames is retired,
    /// so a gap of <paramref name="graceFrames"/> - 1 frames still keeps the id.
    /// </param>
    public Tracker(int graceFrames = DefaultGraceFrames)
    {
        if (graceFrames < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(graceFrames), "Grace frames must be at least 1.");
        }

        GraceFrames = graceFrames;
    }

    /// <summary>Consecutive unmatched frames a track survives before retiring.</summary>
    public int GraceFrames { get; }

    /// <summary>
    /// Associates <paramref name="detections"/> with active tracks and returns the
    /// active tracks only.
    /// Greedy IoU matching: all track/detection pairs at IoU >= <see cref="IouThreshold"/>
    /// are matched in descending IoU order, each track and detection used at most once.
    /// Unmatched detections spawn new tracks (ids monotonic from 1, never reused within
    /// the run). Unmatched tracks increment <see cref="Track.Misses"/> and retire when
    /// misses reach <see cref="GraceFrames"/>.
    /// Frame time: <paramref name="frameTimeUtc"/> is the frame's EVENT time, shared by
    /// every detection in this frame — matched and newly created tracks record it as
    /// <see cref="Track.LastDetectionTimeUtc"/> together with the detection's own
    /// <see cref="Detection.Confidence"/>. Per-detection times are deliberately NOT
    /// passed separately: all detections on one frame come from the same detector pass
    /// over that frame, so one timestamp is exact and keeps the signature honest.
    /// When <paramref name="isResolutionChange"/> is true, ALL active tracks are retired
    /// first: a field-of-view change invalidates identity, so every detection spawns a
    /// new track.
    /// </summary>
    public TrackedFrame Update(
        long frameNumber,
        IReadOnlyList<Detection> detections,
        DateTimeOffset frameTimeUtc,
        bool isResolutionChange = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(detections);
        if (frameNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameNumber), "Frame number must be non-negative.");
        }

        ct.ThrowIfCancellationRequested();

        if (isResolutionChange)
        {
            _activeTracks.Clear();
        }

        var matchedTracks = new HashSet<Track>();
        var matchedDetections = new HashSet<Detection>(ReferenceEqualityComparer.Instance);

        var pairs = new List<(Track Track, Detection Detection, double Iou)>();
        foreach (var track in _activeTracks)
        {
            foreach (var detection in detections)
            {
                ct.ThrowIfCancellationRequested();
                var iou = ComputeIou(track.Box, detection.Box);
                if (iou >= IouThreshold)
                {
                    pairs.Add((track, detection, iou));
                }
            }
        }

        pairs.Sort(static (a, b) => b.Iou.CompareTo(a.Iou));
        foreach (var (track, detection, _) in pairs)
        {
            if (!matchedTracks.Add(track) || !matchedDetections.Add(detection))
            {
                continue;
            }

            track.Match(detection.Box, frameNumber, detection.Confidence, frameTimeUtc);
        }

        foreach (var track in _activeTracks)
        {
            if (!matchedTracks.Contains(track))
            {
                track.Miss();
            }
        }

        var graceFrames = GraceFrames;
        _activeTracks.RemoveAll(track => track.Misses >= graceFrames);

        foreach (var detection in detections)
        {
            if (!matchedDetections.Contains(detection))
            {
                var track = new Track(++_nextId, detection.Box, frameNumber, detection.Confidence, frameTimeUtc);
                _activeTracks.Add(track);
            }
        }

        var snapshot = _activeTracks.OrderBy(static track => track.Id).ToArray();
        return new TrackedFrame(frameNumber, snapshot);
    }

    internal static double ComputeIou(BoundingBox a, BoundingBox b)
    {
        // Area is guaranteed positive by the BoundingBox guards (zero-area boxes are
        // rejected at construction), so no re-validation here; the asserts document
        // the precondition instead of paying for it per pair.
        Debug.Assert(a.W > 0.0 && a.H > 0.0, "BoundingBox guards guarantee positive area.");
        Debug.Assert(b.W > 0.0 && b.H > 0.0, "BoundingBox guards guarantee positive area.");

        var interX1 = Math.Max(a.X, b.X);
        var interY1 = Math.Max(a.Y, b.Y);
        var interX2 = Math.Min(a.X + a.W, b.X + b.W);
        var interY2 = Math.Min(a.Y + a.H, b.Y + b.H);
        var interW = Math.Max(0.0, interX2 - interX1);
        var interH = Math.Max(0.0, interY2 - interY1);
        var intersection = interW * interH;
        if (intersection <= 0.0)
        {
            return 0.0;
        }

        var union = (a.W * a.H) + (b.W * b.H) - intersection;
        return intersection / union;
    }
}
