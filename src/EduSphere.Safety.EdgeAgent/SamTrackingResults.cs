using System.Security.Cryptography;
using System.Text.Json;
using EduSphere.Safety.Edge;

namespace EduSphere.Safety.EdgeAgent;

/// <summary>Reads real SAM API results bound to the exact source video bytes.</summary>
public sealed class SamTrackingResults
{
    private readonly Dictionary<long, IReadOnlyList<ModelDetection>> _frames = [];
    public int FrameCount { get; private init; }

    public static SamTrackingResults Load(string directory, string sourceUrl, double fps)
    {
        var sourcePath = sourceUrl;
        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) && uri.Scheme == "file") sourcePath = uri.LocalPath;
        if (!Path.IsPathFullyQualified(sourcePath))
            throw new InvalidDataException("SAM result ingestion requires the absolute path of the exact recorded source clip.");
        try
        {
            using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "summary.json")));
            var root = summary.RootElement;
            if (root.GetProperty("model").GetString() != "sam-3.1") throw new InvalidDataException("Expected SAM 3.1 results.");
            using var source = File.OpenRead(sourcePath);
            var hash = Convert.ToHexString(SHA256.HashData(source));
            if (!hash.Equals(root.GetProperty("sourceSha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SAM results belong to a different video.");
            var width = root.GetProperty("width").GetInt32(); var height = root.GetProperty("height").GetInt32();
            var frames = root.GetProperty("frames").GetInt32(); var sourceFps = root.GetProperty("fps").GetDouble();
            if (width <= 0 || height <= 0 || frames < 1 || frames > 15000 || !double.IsFinite(sourceFps) || Math.Abs(sourceFps - fps) > 0.000001)
                throw new InvalidDataException("Invalid SAM source metadata or --fps does not match the exact source rate.");
            var result = new SamTrackingResults { FrameCount = frames };
            foreach (var line in File.ReadLines(Path.Combine(directory, "tracks.jsonl")))
            {
                using var doc = JsonDocument.Parse(line);
                var frame = doc.RootElement.GetProperty("frame").GetInt64();
                if (frame < 0 || frame >= frames || result._frames.ContainsKey(frame)) throw new InvalidDataException("Invalid SAM frame number.");
                var detections = new List<ModelDetection>(); var ids = new HashSet<long>();
                foreach (var item in doc.RootElement.GetProperty("tracks").EnumerateArray())
                {
                    var id = item.GetProperty("id").GetInt64();
                    var box = item.GetProperty("box").EnumerateArray().Select(v => v.GetDouble()).ToArray();
                    var confidence = item.GetProperty("confidence").GetDouble();
                    if (id < 0 || id == long.MaxValue || !ids.Add(id) || box.Length != 4 || box.Any(v => !double.IsFinite(v)) ||
                        box[0] < 0 || box[1] < 0 || box[2] > width || box[3] > height || box[2] <= box[0] || box[3] <= box[1] ||
                        !double.IsFinite(confidence) || confidence < 0 || confidence > 1)
                        throw new InvalidDataException("Invalid SAM box, confidence or identity.");
                    detections.Add(new ModelDetection(id, new BoundingBox(box[0] / width, box[1] / height,
                        (box[2] - box[0]) / width, (box[3] - box[1]) / height), confidence));
                }
                if (detections.Count > 16) throw new InvalidDataException("SAM video limit is 16 objects per frame.");
                result._frames.Add(frame, detections);
            }
            return result;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidDataException("Malformed SAM result bundle.", ex);
        }
    }

    public IReadOnlyList<ModelDetection> ForFrame(long frame)
    {
        if (frame < 0 || frame >= FrameCount) throw new InvalidDataException("Decoded frames exceed the SAM source clip.");
        return _frames.GetValueOrDefault(frame) ?? [];
    }
}
