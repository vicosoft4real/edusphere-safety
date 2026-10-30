using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EduSphere.Simulator;

/// <summary>One normalized person box (fractions of frame width/height, 0..1).</summary>
public sealed record PersonBox(double X, double Y, double W, double H);

/// <summary>One scripted frame: its source sequence number plus person boxes on it.</summary>
public sealed record SceneFrame(int FrameIndex, IReadOnlyList<PersonBox> Boxes);

/// <summary>
/// Deterministic scripted scene: maps frame indices to normalized person rects.
/// Pure C# (no Skia dependency) so the manifest is reproducible without native assets.
/// <para>
/// Determinism: every box is a pure function of (seed, frameIndex) through an inline
/// LCG — no <c>System.Random</c> (whose algorithm is version-coupled). The same
/// (scenario, seed, frames, fps, size) input yields a byte-identical manifest, so
/// CI can pin <see cref="ManifestHash"/> as a regression gate.
/// </para>
/// <para>
/// Scenarios: <c>empty</c> (no persons), <c>walker-crossing-zone</c> (one person
/// walking left→right through the corridor-center zone), <c>loiterer</c> (one person
/// standing inside the zone), <c>group</c> (three persons crossing), 
/// <c>bidirectional-boundary</c> (one person crossing x=0.5 left→right then back),
/// <c>dropout-gap</c> (walker with sequence indices 10–14 missing entirely — no PNGs,
/// no manifest entries — modelling a camera dropout gap in numbering).
/// </para>
/// </summary>
public sealed class SceneScript
{
    /// <summary>Corridor-center zone the walker/loiterer scenarios target (normalized).</summary>
    public static readonly double[] ZoneRect = [0.35, 0.0, 0.30, 1.0]; // x, y, w, h

    /// <summary>All supported scenario names.</summary>
    public static readonly IReadOnlyList<string> Scenarios =
        ["empty", "walker-crossing-zone", "loiterer", "group", "bidirectional-boundary", "dropout-gap"];

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private SceneScript(
        string scenario, int seed, int width, int height, int fps,
        IReadOnlyList<SceneFrame> frames)
    {
        Scenario = scenario;
        Seed = seed;
        Width = width;
        Height = height;
        Fps = fps;
        Frames = frames;
    }

    /// <summary>Scenario name.</summary>
    public string Scenario { get; }

    /// <summary>Determinism seed.</summary>
    public int Seed { get; }

    /// <summary>Frame width in pixels.</summary>
    public int Width { get; }

    /// <summary>Frame height in pixels.</summary>
    public int Height { get; }

    /// <summary>Frames per second.</summary>
    public int Fps { get; }

    /// <summary>Present frames only (dropout-gap omits indices).</summary>
    public IReadOnlyList<SceneFrame> Frames { get; }

    /// <summary>
    /// Builds a scene. <paramref name="frameCount"/> is the sequence length;
    /// dropout-gap additionally omits indices 10–14 from it.
    /// </summary>
    public static SceneScript Build(
        string scenario, int seed = 7, int frameCount = 40,
        int width = 640, int height = 480, int fps = 5)
    {
        if (!Scenarios.Contains(scenario))
        {
            throw new ArgumentException($"Unknown scenario: {scenario}.", nameof(scenario));
        }

        if (frameCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount), "Frame count must be positive.");
        }

        var frames = new List<SceneFrame>();
        for (var i = 0; i < frameCount; i++)
        {
            if (scenario == "dropout-gap" && i >= 10 && i <= 14)
            {
                continue; // Camera dropout: no frame, no ground truth, numbering jumps 9 → 15.
            }

            frames.Add(new SceneFrame(i, BoxesAt(scenario, seed, i, frameCount)));
        }

        return new SceneScript(scenario, seed, width, height, fps, frames);
    }

    /// <summary>PNG file name for a frame index (zero-padded for ffmpeg image2).</summary>
    public static string FrameFileName(int frameIndex) => $"f{frameIndex:D4}.png";

    /// <summary>Canonical ground-truth manifest JSON (schema v1, see tools/Simulator/README.md).</summary>
    public string ToManifestJson()
    {
        var doc = new ManifestDoc
        {
            Scenario = Scenario,
            Seed = Seed,
            Width = Width,
            Height = Height,
            Fps = Fps,
            Frames = Frames.Select(f => new ManifestFrame
            {
                FrameIndex = f.FrameIndex,
                Boxes = f.Boxes.Select(b => new ManifestBox { X = b.X, Y = b.Y, W = b.W, H = b.H }).ToArray(),
            }).ToArray(),
        };
        return JsonSerializer.Serialize(doc, ManifestJson);
    }

    /// <summary>SHA256 (lowercase hex) of <see cref="ToManifestJson"/> — the determinism gate.</summary>
    public string ManifestHash() =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ToManifestJson()))).ToLowerInvariant();

    private static IReadOnlyList<PersonBox> BoxesAt(string scenario, int seed, int frame, int frameCount)
    {
        const double w = 0.12;
        const double h = 0.25;
        const double y = 0.55; // Foot at 0.80 — inside the full-height center zone.
        return scenario switch
        {
            "empty" => [],
            "walker-crossing-zone" or "dropout-gap" =>
                [new PersonBox(Lerp(-0.15, 1.02, frame / (double)Math.Max(1, frameCount - 1)), y, w, h)],
            "loiterer" =>
                [new PersonBox(0.44 + (Lcg(seed) % 1000) / 1000.0 * 0.002, y, w, h)],
            "group" =>
            [
                new PersonBox(Lerp(-0.15, 1.02, frame / (double)Math.Max(1, frameCount - 1)), 0.50, w, h),
                new PersonBox(Lerp(-0.30, 0.90, frame / (double)Math.Max(1, frameCount - 1)), 0.58, w, h),
                new PersonBox(Lerp(1.02, -0.15, frame / (double)Math.Max(1, frameCount - 1)), 0.52, w, h),
            ],
            "bidirectional-boundary" =>
                [new PersonBox(frame < frameCount / 2
                    ? Lerp(0.10, 0.90, frame / (double)Math.Max(1, (frameCount / 2) - 1))
                    : Lerp(0.90, 0.10, (frame - (frameCount / 2)) / (double)Math.Max(1, frameCount - (frameCount / 2) - 1)),
                    y, w, h)],
            _ => [],
        };
    }

    private static double Lerp(double from, double to, double t) => from + ((to - from) * t);

    private static uint Lcg(int seed)
    {
        unchecked
        {
            return (uint)(seed * 1664525L + 1013904223L);
        }
    }

    private sealed class ManifestDoc
    {
        [JsonPropertyName("format")]
        public string Format { get; set; } = "edusphere-sim-manifest/1";

        [JsonPropertyName("scenario")]
        public string Scenario { get; set; } = string.Empty;

        [JsonPropertyName("seed")]
        public int Seed { get; set; }

        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        [JsonPropertyName("fps")]
        public int Fps { get; set; }

        [JsonPropertyName("frames")]
        public ManifestFrame[] Frames { get; set; } = [];
    }

    private sealed class ManifestFrame
    {
        [JsonPropertyName("frameIndex")]
        public int FrameIndex { get; set; }

        [JsonPropertyName("boxes")]
        public ManifestBox[] Boxes { get; set; } = [];
    }

    private sealed class ManifestBox
    {
        [JsonPropertyName("x")]
        public double X { get; set; }

        [JsonPropertyName("y")]
        public double Y { get; set; }

        [JsonPropertyName("w")]
        public double W { get; set; }

        [JsonPropertyName("h")]
        public double H { get; set; }
    }
}
