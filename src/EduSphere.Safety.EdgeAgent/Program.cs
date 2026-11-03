using System.Text.Json;
using EduSphere.Safety.Edge;
using EduSphere.Safety.Edge.Onnx;
using EduSphere.Safety.EdgeAgent;

// Thin host: wires IFrameSource -> IPersonDetector -> Tracker -> RuleEngine ->
// outbox JSONL + evidence buffer/spool with pinning. No geometry, no rules, no
// clock reads in libraries — DateTimeOffset.UtcNow appears ONLY here, at the
// process boundary, to stamp ingest/transmit times. Deployable packaging is Plan 4.

static string Arg(string[] args, string name, string fallback)
{
    for (var i = 0; i + 1 < args.Length; i++)
    {
        if (args[i] == name)
        {
            return args[i + 1];
        }
    }

    return fallback;
}

static int ArgInt(string[] args, string name, int fallback) =>
    int.TryParse(Arg(args, name, fallback.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        System.Globalization.NumberStyles.Integer,
        System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

static double ArgDouble(string[] args, string name, double fallback) =>
    double.TryParse(Arg(args, name, fallback.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

static string Req(string[] args, string name)
{
    var value = Arg(args, name, string.Empty);
    if (string.IsNullOrWhiteSpace(value))
    {
        Console.Error.WriteLine($"Missing required arg: {name}.");
        Console.Error.WriteLine("Usage: edgeagent --source <url> --camera <id> --rules <bundle.json> [--manifest <manifest.json> | --model <model.onnx> --model-sha256 <hex> --pixel-divisor <f>] --out <dir> [options]");
        Environment.Exit(2);
    }

    return value;
}

var sourceUrl = Req(args, "--source");
var cameraId = Req(args, "--camera");
var rulesPath = Req(args, "--rules");
var modelPath = Arg(args, "--model", string.Empty);
var manifestPath = Arg(args, "--manifest", string.Empty);
var outDir = Req(args, "--out");
var width = ArgInt(args, "--width", 640);
var height = ArgInt(args, "--height", 480);
var fps = ArgDouble(args, "--fps", 5);
if (!double.IsFinite(fps) || fps <= 0.0)
{
    Console.Error.WriteLine("Refusing to start: bad --fps (must be a finite positive number).");
    return 2;
}
var ffmpeg = Arg(args, "--ffmpeg", "ffmpeg");
var transport = Arg(args, "--transport", "tcp");
var grace = ArgInt(args, "--grace", 5);
var firstFrameTime = DateTimeOffset.Parse(
    Arg(args, "--first-frame-time", "2026-01-01T00:00:00Z"),
    System.Globalization.CultureInfo.InvariantCulture);
var keyHex = Arg(args, "--evidence-key-hex",
    Environment.GetEnvironmentVariable("EDGESPHERE_EVIDENCE_KEY_HEX") ?? string.Empty);

Directory.CreateDirectory(outDir);
var outbox = new OutboxWriter(Path.Combine(outDir, "outbox.jsonl"));
var spool = new UploadSpool(Path.Combine(outDir, "spool.jsonl"));

EvidenceBuffer? buffer = null;
if (!string.IsNullOrWhiteSpace(keyHex))
{
    byte[] key;
    try
    {
        key = Convert.FromHexString(keyHex.Trim());
    }
    catch (FormatException)
    {
        Console.Error.WriteLine("Evidence key must be 64 hex chars (32 bytes).");
        return 2;
    }

    if (key.Length != 32)
    {
        Console.Error.WriteLine("Evidence key must be 64 hex chars (32 bytes).");
        return 2;
    }

    buffer = new EvidenceBuffer(Path.Combine(outDir, "evidence"), key, boundedBytes: 256L * 1024 * 1024);
}

RuleBundle bundle;
try
{
    bundle = RuleBundleLoader.LoadFromFile(rulesPath);
}
catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Refusing to start: bad rule bundle ({ex.Message}).");
    return 2;
}

// This standalone process owns the native environment. Dispose all sessions
// before the environment, rather than leaving native cleanup to process exit.
using var ortEnvironment = !string.IsNullOrWhiteSpace(modelPath)
    ? Microsoft.ML.OnnxRuntime.OrtEnv.Instance() : null;
IPersonDetector detector;
if (!string.IsNullOrWhiteSpace(modelPath))
{
    // Live detection: real pixels through the ONNX model. Weights checksum is
    // mandatory (refuse-to-start on mismatch); pixelDivisor must match the
    // frozen export's preprocessing (YOLOX-style raw pixels: 1.0).
    var modelSha = Arg(args, "--model-sha256", string.Empty);
    var divisorRaw = Arg(args, "--pixel-divisor", string.Empty);
    var confidence = ArgDouble(args, "--confidence", 0.5);
    var threads = ArgInt(args, "--intra-op-threads", 1);
    var inputSize = ArgInt(args, "--input-size", YoloxOutputParser.SupportedInputSize);
    var outputEncoding = Arg(args, "--model-output", "raw-grid");
    if (outputEncoding is not ("raw-grid" or "decoded"))
    {
        Console.Error.WriteLine("--model-output must be raw-grid or decoded.");
        return 2;
    }
    if (string.IsNullOrWhiteSpace(modelSha) || !double.TryParse(
            divisorRaw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var divisor))
    {
        Console.Error.WriteLine("Refusing to start: --model requires --model-sha256 and --pixel-divisor.");
        return 2;
    }

    try
    {
        detector = new OnnxPersonDetector(modelPath, modelSha, divisor, inputSize, confidenceThreshold: confidence, intraOpThreads: threads, rawGridOutput: outputEncoding == "raw-grid");
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Refusing to start: bad model ({ex.Message}).");
        return 2;
    }
}
else
{
    if (string.IsNullOrWhiteSpace(manifestPath))
    {
        Console.Error.WriteLine("Refusing to start: need --manifest (replay) or --model (live detection).");
        Console.Error.WriteLine("Usage: edgeagent --source <url> --camera <id> --rules <bundle.json> [--manifest <manifest.json> | --model <model.onnx> --model-sha256 <hex> --pixel-divisor <f>] --out <dir> [options]");
        return 2;
    }

    try
    {
        detector = ManifestReplayDetector.LoadFromFile(manifestPath);
    }
    catch (Exception ex) when (ex is InvalidDataException or JsonException or FormatException
        or IOException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException)
    {
        Console.Error.WriteLine($"Refusing to start: bad manifest ({ex.Message}).");
        return 2;
    }
}

using var detectorLifetime = detector as IDisposable;
var tracker = new Tracker(grace);
var engine = new RuleEngine(bundle.Rules, new ScheduleEvaluator(), bundle.SchedulesById);

var lastEventTime = firstFrameTime;
var monitor = new FrameHealthMonitor(
    emit: e => outbox.AppendHealth(cameraId, e, DateTimeOffset.UtcNow),
    frozenThreshold: 30,
    streamLostTimeout: TimeSpan.FromSeconds(10));

var exitNoted = false;
using var source = new FfmpegFrameSource(
    cameraId, sourceUrl, width, height, fps, firstFrameTime, ffmpeg, transport,
    logStderr: line => Console.Error.WriteLine($"[ffmpeg] {line}"),
    onStreamDown: reason =>
    {
        // Edge-triggered by source contract (once per run): crash reasons feed the
        // monitor; clean EOF is just end-of-evidence for file mode.
        if (reason.StartsWith("ffmpeg-exit:", StringComparison.Ordinal) &&
            int.TryParse(reason["ffmpeg-exit:".Length..], out var code) && !exitNoted)
        {
            exitNoted = true;
            monitor.NoteProcessExit(code, lastEventTime);
        }

        outbox.AppendOpsNote(cameraId, "stream-down", reason);
    });

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

long frames = 0;
long events = 0;
await foreach (var frame in source.GetFramesAsync(cts.Token))
{
    lastEventTime = frame.CapturedAtUtc;
    // Pinned wiring: the tracker's ActiveTracks ARE the presence signal — passed
    // through unfiltered (even grace-held stale boxes), per RuleEngine.Evaluate docs.
    monitor.NoteFrame(source.LastFrameHash ?? 0, streamOpen: true, frame.CapturedAtUtc);
    var detections = await detector.DetectAsync(frame, cts.Token);
    var tracked = tracker.Update(frame.Number, detections, frame.CapturedAtUtc, frame.IsResolutionChange);
    var scored = tracked.ActiveTracks
        .Select(t => new ScoredTrack(t, t.Confidence, t.LastDetectionTimeUtc))
        .ToArray();
    engine.NoteHealthyFrame(frame.CapturedAtUtc);
    var fired = engine.Evaluate(frame.CapturedAtUtc, scored, bundle.ModelVersion, cts.Token);
    foreach (var e in fired)
    {
        e.IngestTimeUtc = DateTimeOffset.UtcNow; // Host-boundary stamp; engine never sets this.
        var id = outbox.Append(cameraId, e);
        string? segmentId = null;
        if (buffer is not null)
        {
            // PLACEHOLDER payload (JSON descriptor, not video): Plan 4 muxes a real
            // MP4 clip here. The pinning contract is already real: enqueue pins,
            // successful upload unpins; entries stay queued until the Plan 4 drain.
            var clipDescriptor = JsonSerializer.SerializeToUtf8Bytes(new
            {
                clip = "placeholder-descriptor",
                cameraId,
                ruleId = e.RuleId,
                eventTimeUtc = e.EventTimeUtc,
                note = "Plan 4 replaces this descriptor with a muxed MP4 clip.",
            });
            segmentId = buffer.AppendSegment(e.EventTimeUtc, e.EventTimeUtc, clipDescriptor);
            buffer.Pin(segmentId);
        }

        spool.Enqueue(new PendingUpload(
            id, e.RuleId, e.RuleVersion, e.TrackId,
            e.EventTimeUtc, e.DetectionTimeUtc, segmentId, AttemptCount: 0));
        events++;
    }

    monitor.QueryIdle(frame.CapturedAtUtc);
    frames++;
}

// Single verification pass over the queued spool: a clip the buffer no longer has
// is DROPPED with an ops note — translated once, never retried forever. Drop covers
// every "gone" shape GetSegment (or the filesystem under it) can report: unknown id
// (KeyNotFoundException), missing segment file (FileNotFoundException), missing
// directory (DirectoryNotFoundException), other IO failures (IOException), and
// permission loss (UnauthorizedAccessException). Tamper/integrity failures
// (CryptographicException) are NOT dropped here — they propagate so a corrupt
// evidence store is loud, not silently discarded. No uploader runs in this host;
// the Plan 4 drain removes entries on successful upload and unpins their segments.
foreach (var pending in spool.All.ToArray())
{
    if (pending.SegmentId is null || buffer is null)
    {
        continue;
    }

    try
    {
        buffer.GetSegment(pending.SegmentId);
    }
    catch (Exception ex) when (ex is KeyNotFoundException or FileNotFoundException
        or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
    {
        spool.Remove(pending.EdgeEventId);
        outbox.AppendOpsNote(cameraId, "spool-drop", $"{pending.EdgeEventId}: {ex.GetType().Name}");
    }
}

Console.WriteLine($"done camera={cameraId} frames={frames} events={events} queued={spool.Count}");
return 0;
