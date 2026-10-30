using System.Text.Json;
using EduSphere.Safety.Edge;

namespace EduSphere.Safety.EdgeAgent;

/// <summary>
/// Scripted stand-in for the future ONNX detector: replays per-frame boxes from a
/// ground-truth manifest (<c>edusphere-sim-manifest/1</c>, see tools/Simulator/README.md).
/// <para>
/// ORDER-BASED replay (not frame-number lookup): the Nth <c>DetectAsync</c> call serves
/// the Nth manifest entry in <c>frameIndex</c> order. Mux renumbers frames — a dropout
/// gap survives muxing as a SHORTER clip (decoded frames 0..N map to the N present
/// PNGs, not to source indices), so a number-keyed lookup would misattribute every
/// detection after a gap. Order mapping keeps attribution across that renumbering.
/// </para>
/// <para>
/// ASSUMPTION (file mode): no mid-stream frame drops — every decoded frame corresponds
/// to the next manifest entry in order. A dropped decoded frame would shift all later
/// attribution by one; file-mode FFmpeg decode of an intact MP4 does not drop frames,
/// so this holds for the simulator proof. Live/RTSP drops are a Plan 4 concern.
/// </para>
/// <para>
/// PIXELS IGNORED BY DESIGN: this detector never reads <c>Frame.Pixels</c> — attribution
/// is manifest-keyed, which is exactly the fidelity boundary the simulator proves
/// (decode → track → rules plumbing, not visual detection). A real detector reads the
/// same pixels through the unchanged <c>IPersonDetector</c> signature.
/// </para>
/// <para>
/// Like a real detector, only VISIBLE boxes are emitted: scripted boxes that extend
/// off-screen are clamped to the frame, and fully off-screen boxes are dropped.
/// Emitted confidence is a fixed detector-signal constant
/// (<see cref="ReplayConfidence"/>) used for thresholding only — NOT a danger probability.
/// </para>
/// </summary>
public sealed class ManifestReplayDetector : IPersonDetector
{
    /// <summary>Fixed detector-signal confidence for replayed boxes (thresholding only).</summary>
    public const double ReplayConfidence = 0.9;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IReadOnlyList<IReadOnlyList<Detection>> _ordered;
    private int _served;

    /// <summary>
    /// Creates a replay detector from already-loaded per-frame boxes. Entries replay in
    /// ascending frame-number order; the numbers themselves are then ignored.
    /// </summary>
    public ManifestReplayDetector(IReadOnlyDictionary<long, IReadOnlyList<Detection>> byFrame)
    {
        ArgumentNullException.ThrowIfNull(byFrame);
        _ordered = byFrame.OrderBy(static kv => kv.Key).Select(static kv => kv.Value).ToArray();
    }

    /// <summary>Loads a detector from a manifest JSON file (schema v1).</summary>
    /// <exception cref="InvalidDataException">Not schema v1, or malformed entries.</exception>
    public static ManifestReplayDetector LoadFromFile(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = doc.RootElement;
            if (!root.TryGetProperty("format", out var format) ||
                format.GetString() != "edusphere-sim-manifest/1")
            {
                throw new InvalidDataException("Manifest is not edusphere-sim-manifest/1.");
            }

            var byFrame = new Dictionary<long, IReadOnlyList<Detection>>();
            foreach (var frame in root.GetProperty("frames").EnumerateArray())
            {
                var index = frame.GetProperty("frameIndex").GetInt64();
                var detections = new List<Detection>();
                foreach (var box in frame.GetProperty("boxes").EnumerateArray())
                {
                    if (TryClampToVisible(
                            box.GetProperty("x").GetDouble(),
                            box.GetProperty("y").GetDouble(),
                            box.GetProperty("w").GetDouble(),
                            box.GetProperty("h").GetDouble(),
                            out var clamped))
                    {
                        detections.Add(new Detection(clamped, ReplayConfidence, Detection.PersonClassId));
                    }
                }

                byFrame[index] = detections;
            }

            return new ManifestReplayDetector(byFrame);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException($"Ground-truth manifest is malformed ({ex.GetType().Name}).", ex);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Detection>> DetectAsync(Frame frame, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ct.ThrowIfCancellationRequested();
        // Nth decoded frame → Nth manifest entry. Past the end (fewer entries than
        // decoded frames): no detections, like a real detector seeing empty frames.
        var index = Interlocked.Increment(ref _served) - 1;
        IReadOnlyList<Detection> detections =
            (uint)index < (uint)_ordered.Count ? _ordered[index] : [];
        return Task.FromResult(detections);
    }

    internal static bool TryClampToVisible(double x, double y, double w, double h, out BoundingBox clamped)
    {
        var x2 = Math.Min(1.0, x + w);
        var y2 = Math.Min(1.0, y + h);
        var cx = Math.Max(0.0, x);
        var cy = Math.Max(0.0, y);
        if (x2 - cx <= 1e-9 || y2 - cy <= 1e-9)
        {
            clamped = null!;
            return false; // Fully off-screen: a real detector emits nothing.
        }

        clamped = new BoundingBox(cx, cy, x2 - cx, y2 - cy);
        return true;
    }
}
