namespace EduSphere.Safety.Edge;

/// <summary>
/// Produces the frame stream for one camera. Implementations MUST yield frames with
/// strictly increasing <see cref="Frame.Number"/> values (the monotonic-numbers contract;
/// enforced here at the source, since <see cref="Frame"/> construction alone cannot check
/// cross-frame ordering), and MUST set <see cref="Frame.IsResolutionChange"/> on the first
/// frame after a stream geometry change so downstream stages can reset tracks.
/// At-least-once flag forwarding: the source sets <see cref="Frame.IsResolutionChange"/>
/// on exactly one frame per geometry change (the first frame after the change), and every
/// downstream pipeline stage MUST propagate the flag even when it filters frames — a stage
/// that drops the flagged frame must set the flag on the next frame it emits, so the
/// track-reset signal is never silently swallowed.
/// </summary>
public interface IFrameSource
{
    /// <summary>Stable camera identifier; matches <see cref="Frame.CameraId"/> on every yielded frame.</summary>
    string CameraId { get; }

    /// <summary>Yields frames with strictly increasing numbers until cancellation.</summary>
    IAsyncEnumerable<Frame> GetFramesAsync(CancellationToken ct);
}

/// <summary>
/// Detects persons on a single frame. Returns person detections only
/// (<see cref="Detection.ClassId"/> == <see cref="Detection.PersonClassId"/>),
/// already filtered by the implementation's configured confidence threshold.
/// <para>
/// PIXEL CONTRACT: the detector receives pixels via <see cref="Frame.Pixels"/>
/// (documented <c>Rgb24</c> row-major layout, <c>Length == Width * Height * 3</c>
/// when present) — the signature is unchanged on purpose so current and future
/// detectors (scripted replay, ONNX) share one seam. A detector MAY ignore the
/// payload (the manifest replay detector does: attribution is manifest-keyed),
/// but it MUST NOT assume a non-empty payload — scripted/test frames carry none.
/// </para>
/// </summary>
public interface IPersonDetector
{
    /// <summary>Runs person detection on <paramref name="frame"/>.</summary>
    /// <param name="frame">The frame to detect on; must not be null (null ⇒ <see cref="ArgumentNullException"/>).</param>
    /// <returns>
    /// Person detections for <paramref name="frame"/>, already filtered by the
    /// implementation's configured confidence threshold. Never null: an empty list when
    /// nothing is found. Results correspond to the passed frame; callers must not reorder them.
    /// </returns>
    Task<IReadOnlyList<Detection>> DetectAsync(Frame frame, CancellationToken ct);
}
