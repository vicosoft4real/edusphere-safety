namespace EduSphere.Safety.Edge;

public sealed record ModelDetection(long ModelId, BoundingBox Box, double Confidence);

/// <summary>Preserves provider IDs within one response; never performs IoU reassignment.</summary>
public sealed class ModelTrackProjector
{
    private readonly Dictionary<long, Track> _tracks = [];
    private long _lastFrame = -1;

    public TrackedFrame Update(long frame, IReadOnlyList<ModelDetection> detections, DateTimeOffset time)
    {
        ArgumentNullException.ThrowIfNull(detections);
        if (frame < 0 || frame <= _lastFrame) throw new ArgumentOutOfRangeException(nameof(frame));
        var ids = new HashSet<long>();
        foreach (var d in detections)
        {
            if (d.ModelId < 0 || d.ModelId == long.MaxValue || !ids.Add(d.ModelId))
                throw new ArgumentException("Provider IDs must be non-negative, unique and safely map to core IDs.");
            if (d.Box is null || !double.IsFinite(d.Confidence) || d.Confidence < 0 || d.Confidence > 1)
                throw new ArgumentException("Invalid provider detection.");
        }
        // A skipped/missing frame cannot contribute a presence signal or a trail
        // segment crossing unseen space. Model IDs still retain their own meaning.
        if (frame != _lastFrame + 1) _tracks.Clear();
        foreach (var id in _tracks.Keys.Except(ids).ToArray()) _tracks.Remove(id);
        foreach (var d in detections)
        {
            if (_tracks.TryGetValue(d.ModelId, out var track)) track.Match(d.Box, frame, d.Confidence, time);
            else _tracks[d.ModelId] = new Track(d.ModelId + 1, d.Box, frame, d.Confidence, time);
        }
        _lastFrame = frame;
        return new TrackedFrame(frame, _tracks.Values.OrderBy(t => t.Id).ToArray());
    }
}
