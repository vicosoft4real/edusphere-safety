using System.Diagnostics;
using System.Text.Json;
using EduSphere.Safety.Edge;
using EduSphere.Safety.EdgeAgent;
using EduSphere.Simulator;
using Xunit;

public sealed class EdgePipelineTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- simulator determinism ----

    [Fact]
    public void SceneScript_SameSeed_IdenticalManifestHash()
    {
        var a = SceneScript.Build("walker-crossing-zone", seed: 7);
        var b = SceneScript.Build("walker-crossing-zone", seed: 7);
        Assert.Equal(a.ToManifestJson(), b.ToManifestJson());
        Assert.Equal(a.ManifestHash(), b.ManifestHash());
    }

    [Fact]
    public void SceneScript_DifferentSeed_DifferentManifestHash()
    {
        var a = SceneScript.Build("walker-crossing-zone", seed: 7);
        var b = SceneScript.Build("walker-crossing-zone", seed: 8);
        Assert.NotEqual(a.ManifestHash(), b.ManifestHash());
    }

    // ---- golden manifest hashes: renderer changes must consciously update these ----
    //
    // One pinned SHA256 per scenario (default seed 7 / 40 frames / 640x480 / 5 fps).
    // If a renderer edit changes any hash, the edit is either a bug or an intended
    // visual change — update the goldens deliberately in the same commit, never
    // blindly paste new values to make the suite green.

    [Theory]
    [InlineData("empty", "69b2d2914ad1dc8c218ac2f9c877444a294d036bf0fd34a55d2870145f7d632e")]
    [InlineData("walker-crossing-zone", "ab63d78725467852ba15fa9258535034ede9ebe71b2539ad39acf89a49ba5cfc")]
    [InlineData("loiterer", "371aa634e88d58a1643a8b1770375485686456f7dce6dcce77fb4c00b9365540")]
    [InlineData("group", "a9bcdb90d73204b5b459b1ee8ca302e5f15c6f0f5b98228bdb30fccc45d2d309")]
    [InlineData("bidirectional-boundary", "06a2cd2acfc07995b2225bc9d5db511e3ef147dcd28cf7b441cf2f2513da13a3")]
    [InlineData("dropout-gap", "1ff549a18d61aefe942dd9064c4a4ab7a5b14665ebf8673649f8f8c1e07137b2")]
    public void SceneScript_GoldenManifestHash_PinnedPerScenario(string scenario, string golden)
    {
        Assert.Equal(golden, SceneScript.Build(scenario, seed: 7).ManifestHash());
    }

    // ---- ground-truth manifest schema ----

    [Fact]
    public void Manifest_Schema_HasRequiredFields()
    {
        var script = SceneScript.Build("walker-crossing-zone", seed: 7);
        using var doc = JsonDocument.Parse(script.ToManifestJson());
        var root = doc.RootElement;
        Assert.Equal("edusphere-sim-manifest/1", root.GetProperty("format").GetString());
        Assert.Equal("walker-crossing-zone", root.GetProperty("scenario").GetString());
        Assert.Equal(7, root.GetProperty("seed").GetInt32());
        Assert.Equal(640, root.GetProperty("width").GetInt32());
        Assert.Equal(480, root.GetProperty("height").GetInt32());
        Assert.Equal(5, root.GetProperty("fps").GetInt32());

        var frames = root.GetProperty("frames").EnumerateArray().ToArray();
        Assert.Equal(40, frames.Length);
        foreach (var frame in frames)
        {
            Assert.True(frame.TryGetProperty("frameIndex", out _));
            foreach (var box in frame.GetProperty("boxes").EnumerateArray())
            {
                foreach (var prop in new[] { "x", "y", "w", "h" })
                {
                    var value = box.GetProperty(prop).GetDouble();
                    Assert.True(double.IsFinite(value));
                }
            }
        }
    }

    [Fact]
    public void Manifest_DropoutGap_OmitsIndices()
    {
        var script = SceneScript.Build("dropout-gap", seed: 7, frameCount: 30);
        var indices = script.Frames.Select(static f => f.FrameIndex).ToArray();
        Assert.Equal(25, indices.Length); // 30 minus the 10–14 gap
        Assert.DoesNotContain(10, indices);
        Assert.DoesNotContain(14, indices);
        Assert.Equal(9, indices[9]);
        Assert.Equal(15, indices[10]); // numbering jumps 9 → 15
    }

    // ---- replay detector: ORDER-based replay + visible-box clamping ----

    [Fact]
    public async Task ReplayDetector_ReplaysInManifestOrder_RegardlessOfFrameNumber()
    {
        var manifest = """
            {"format":"edusphere-sim-manifest/1","scenario":"t","seed":1,"width":640,"height":480,"fps":5,
             "frames":[
               {"frameIndex":0,"boxes":[{"x":-0.5,"y":0.5,"w":0.1,"h":0.2}]},
               {"frameIndex":1,"boxes":[{"x":-0.05,"y":0.5,"w":0.12,"h":0.25}]},
               {"frameIndex":3,"boxes":[{"x":0.4,"y":0.5,"w":0.12,"h":0.25}]}
             ]}
            """;
        var path = Path.Combine(NewTempDir(), "manifest.json");
        await File.WriteAllTextAsync(path, manifest);
        var detector = ManifestReplayDetector.LoadFromFile(path);

        // Decoded frames arrive renumbered 0,1,2 (mux numbering), NOT 0,1,3.
        static Frame At(long n) => new("cam-1", n, T0, 640, 480);
        Assert.Empty(await detector.DetectAsync(At(0), CancellationToken.None)); // entry 0: fully off-screen

        var clamped = await detector.DetectAsync(At(1), CancellationToken.None); // entry 1: partial box
        var only = Assert.Single(clamped);
        Assert.Equal(0.0, only.Box.X);
        Assert.Equal(0.07, only.Box.W, precision: 9);
        Assert.Equal(ManifestReplayDetector.ReplayConfidence, only.Confidence);

        // Third DECODED frame gets the THIRD manifest entry (frameIndex 3) — a
        // frame-number lookup would find no entry 2 and drop this detection.
        var renumbered = await detector.DetectAsync(At(2), CancellationToken.None);
        var moved = Assert.Single(renumbered);
        Assert.Equal(0.4, moved.Box.X, precision: 9);
    }

    [Fact]
    public async Task ReplayDetector_DropoutGap_ReplaysInOrder()
    {
        // Manifest numbering jumps 9 → 15 (camera dropout gap); after mux the clip
        // is shorter and decoded frames arrive renumbered 0..3, so order mapping is
        // what keeps attribution: the frame after entry 9 gets entry 15's boxes.
        // A frame-number lookup would serve [0.1, 0.2, EMPTY, EMPTY] here instead.
        var manifest = """
            {"format":"edusphere-sim-manifest/1","scenario":"dropout-gap","seed":7,"width":640,"height":480,"fps":5,
             "frames":[
               {"frameIndex":8,"boxes":[{"x":0.1,"y":0.5,"w":0.12,"h":0.25}]},
               {"frameIndex":9,"boxes":[{"x":0.2,"y":0.5,"w":0.12,"h":0.25}]},
               {"frameIndex":15,"boxes":[{"x":0.8,"y":0.5,"w":0.12,"h":0.25}]},
               {"frameIndex":16,"boxes":[{"x":0.9,"y":0.5,"w":0.02,"h":0.25}]}
             ]}
            """;
        var path = Path.Combine(NewTempDir(), "manifest.json");
        await File.WriteAllTextAsync(path, manifest);
        var detector = ManifestReplayDetector.LoadFromFile(path);

        var seen = new List<double>();
        for (long n = 0; n < 4; n++)
        {
            var detections = await detector.DetectAsync(
                new Frame("cam-1", n, T0.AddSeconds(n / 5.0), 640, 480), CancellationToken.None);
            seen.Add(Assert.Single(detections).Box.X);
        }

        Assert.Equal([0.1, 0.2, 0.8, 0.9], seen);
    }

    [Fact]
    public async Task ReplayDetector_PastEnd_ReturnsEmpty()
    {
        var detector = new ManifestReplayDetector(
            new Dictionary<long, IReadOnlyList<Detection>>
            {
                [5] = [new Detection(new BoundingBox(0.4, 0.5, 0.12, 0.25), 0.9, Detection.PersonClassId)],
            });

        static Frame At(long n) => new("cam-1", n, T0, 640, 480);
        Assert.Single(await detector.DetectAsync(At(0), CancellationToken.None));
        Assert.Empty(await detector.DetectAsync(At(1), CancellationToken.None));
        Assert.Empty(await detector.DetectAsync(At(2), CancellationToken.None));
    }

    [Fact]
    public void ReplayDetector_BadFormat_Throws()
    {
        var path = Path.Combine(NewTempDir(), "manifest.json");
        File.WriteAllText(path, """{"format":"nope/0","frames":[]}""");
        Assert.Throws<InvalidDataException>(() => ManifestReplayDetector.LoadFromFile(path));
    }

    [Fact]
    public void ReplayDetector_MalformedEntry_WrappedAsInvalidData()
    {
        var dir = NewTempDir();
        var badIndex = Path.Combine(dir, "manifest.json");
        File.WriteAllText(badIndex, """{"format":"edusphere-sim-manifest/1","frames":[{"frameIndex":"NaN","boxes":[]}]}""");
        Assert.Throws<InvalidDataException>(() => ManifestReplayDetector.LoadFromFile(badIndex));

        var missingFrames = Path.Combine(dir, "manifest2.json");
        File.WriteAllText(missingFrames, """{"format":"edusphere-sim-manifest/1"}""");
        Assert.Throws<InvalidDataException>(() => ManifestReplayDetector.LoadFromFile(missingFrames));
    }

    // ---- rule bundle version check ----

    [Fact]
    public void RuleBundle_BadFormatOrVersion_RefusesToLoad()
    {
        var dir = NewTempDir();
        var badFormat = Path.Combine(dir, "bad-format.json");
        File.WriteAllText(badFormat, """{"format":"edusphere-rule-bundle/0","bundleVersion":1,"modelVersion":"m","rules":[]}""");
        Assert.Throws<InvalidDataException>(() => RuleBundleLoader.LoadFromFile(badFormat));

        var badVersion = Path.Combine(dir, "bad-version.json");
        File.WriteAllText(badVersion, """{"format":"edusphere-rule-bundle/1","bundleVersion":0,"modelVersion":"m","rules":[]}""");
        Assert.Throws<InvalidDataException>(() => RuleBundleLoader.LoadFromFile(badVersion));
    }

    [Fact]
    public void RuleBundle_ZoneBundle_LoadsWithSchedule()
    {
        var bundle = RuleBundleLoader.LoadFromFile(WriteZoneBundle(NewTempDir()));
        Assert.Equal(1, bundle.BundleVersion);
        Assert.Equal("sim-detector/1", bundle.ModelVersion);
        var rule = Assert.Single(bundle.Rules);
        Assert.Equal("zone-a", rule.RuleId);
        Assert.Equal(1, rule.Version);
        Assert.True(bundle.SchedulesById.ContainsKey("night"));
        Assert.Equal("UTC", bundle.SchedulesById["night"].IanaTimezone);
    }

    // ---- rule bundle strictness: every violation refuses to start ----

    private static string WriteBundle(string dir, string name, string json)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, json);
        return path;
    }

    private static string ZoneRuleJson(
        string ruleExtra = "",
        string scheduleExtra = "") =>
        $$"""
        {"format":"edusphere-rule-bundle/1","bundleVersion":1,"modelVersion":"sim-detector/1",
         "schedules":[{"scheduleId":"night","ianaTimezone":"UTC","closedWindows":[{"start":"00:00","end":"00:00"}]{{scheduleExtra}}}],
         "rules":[{"ruleId":"zone-a","type":"ZoneIntrusion","version":1,
           "confidenceThreshold":0.5,"persistenceFrames":3,"cooldownSeconds":3600,
           "polygon":[[0.35,0.0],[0.65,0.0],[0.65,1.0],[0.35,1.0]],
           "scheduleId":"night"{{ruleExtra}}}]}
        """;

    [Fact]
    public void RuleBundle_MalformedDate_WrappedAsInvalidData()
    {
        var dir = NewTempDir();
        var path = WriteBundle(dir, "rules.json", ZoneRuleJson(scheduleExtra: ""","holidays":["2026-13-99"]"""));
        Assert.Throws<InvalidDataException>(() => RuleBundleLoader.LoadFromFile(path));

        var badWindow = WriteBundle(dir, "rules2.json",
            ZoneRuleJson().Replace("\"start\":\"00:00\"", "\"start\":\"25:00\""));
        Assert.Throws<InvalidDataException>(() => RuleBundleLoader.LoadFromFile(badWindow));
    }

    [Fact]
    public void RuleBundle_DuplicateScheduleId_Rejected()
    {
        var dir = NewTempDir();
        var path = WriteBundle(dir, "rules.json",
            """{"format":"edusphere-rule-bundle/1","bundleVersion":1,"modelVersion":"m", "schedules":[{"scheduleId":"night"},{"scheduleId":"night"}], "rules":[{"ruleId":"zone-a","type":"ZoneIntrusion","version":1, "polygon":[[0.35,0.0],[0.65,0.0],[0.65,1.0],[0.35,1.0]]}]}""");
        Assert.Throws<InvalidDataException>(() => RuleBundleLoader.LoadFromFile(path));
    }

    [Fact]
    public void RuleBundle_UnknownScheduleOrPoolId_Rejected()
    {
        var dir = NewTempDir();
        // NOTE: ruleExtra appends a second scheduleId member; System.Text.Json
        // takes the last duplicate property, so the rule resolves to "dawn".
        var badSchedule = WriteBundle(dir, "rules.json", ZoneRuleJson(ruleExtra: """ ,"scheduleId":"dawn" """));
        Assert.Throws<InvalidDataException>(() => RuleBundleLoader.LoadFromFile(badSchedule));

        var badPool = WriteBundle(dir, "rules2.json", ZoneRuleJson(ruleExtra: """ ,"poolId":"pool-x" """));
        Assert.Throws<InvalidDataException>(() => RuleBundleLoader.LoadFromFile(badPool));
    }

    [Fact]
    public void RuleBundle_BadGeometryOrThreshold_Rejected()
    {
        var dir = NewTempDir();
        // Polygon with 2 vertices.
        var thin = WriteBundle(dir, "thin.json", ZoneRuleJson().Replace(
            "[[0.35,0.0],[0.65,0.0],[0.65,1.0],[0.35,1.0]]", "[[0.35,0.0],[0.65,0.0]]"));
        Assert.Throws<InvalidDataException>(() => RuleBundleLoader.LoadFromFile(thin));

        // Non-normalized vertex.
        var wild = WriteBundle(dir, "wild.json", ZoneRuleJson().Replace(
            "[0.65,1.0]", "[0.65,5.0]"));
        Assert.Throws<InvalidDataException>(() => RuleBundleLoader.LoadFromFile(wild));

        // Threshold outside 0..1.
        var loud = WriteBundle(dir, "loud.json", ZoneRuleJson().Replace(
            "\"confidenceThreshold\":0.5", "\"confidenceThreshold\":1.5"));
        Assert.Throws<InvalidDataException>(() => RuleBundleLoader.LoadFromFile(loud));

        // Malformed vertex shape.
        var ragged = WriteBundle(dir, "ragged.json", ZoneRuleJson().Replace(
            "[0.65,1.0]", "[0.65]"));
        Assert.Throws<InvalidDataException>(() => RuleBundleLoader.LoadFromFile(ragged));
    }

    [Fact]
    public void RuleBundle_UnknownMembers_Ignored()
    {
        // Forward-compatible bundles (extra members) load; only documented members act.
        var dir = NewTempDir();
        var path = WriteBundle(dir, "rules.json", ZoneRuleJson(
            ruleExtra: ""","futureField":{"whatever":[1,2,3]}""",
            scheduleExtra: """ ,"owner":"facilities" """));
        var bundle = RuleBundleLoader.LoadFromFile(path);
        Assert.Single(bundle.Rules);
    }

    internal static string WriteZoneBundle(string dir)
    {
        var path = Path.Combine(dir, "rules.json");
        File.WriteAllText(path, """
            {"format":"edusphere-rule-bundle/1","bundleVersion":1,"modelVersion":"sim-detector/1",
             "schedules":[{"scheduleId":"night","ianaTimezone":"UTC","closedWindows":[{"start":"00:00","end":"00:00"}]}],
             "rules":[{"ruleId":"zone-a","type":"ZoneIntrusion","version":1,
               "confidenceThreshold":0.5,"persistenceFrames":3,"cooldownSeconds":3600,
               "polygon":[[0.35,0.0],[0.65,0.0],[0.65,1.0],[0.35,1.0]],
               "scheduleId":"night"}]}
            """);
        return path;
    }

    // ---- synthetic pipeline (no ffmpeg/Skia): one intrusion → one event ----

    [Fact]
    public void Pipeline_SyntheticIntrusion_FiresExactlyOneVersionedEvent()
    {
        var engine = ZoneEngine(persistenceFrames: 3);
        var tracker = new Tracker(graceFrames: 5);
        var inside = new Detection(new BoundingBox(0.44, 0.55, 0.12, 0.25), 0.9, Detection.PersonClassId);
        var fired = new List<RuleEvent>();

        for (long n = 0; n < 12; n++)
        {
            var at = T0.AddSeconds(n / 5.0);
            var detections = (n >= 3 && n <= 8) ? (IReadOnlyList<Detection>)[inside] : [];
            var tracked = tracker.Update(n, detections, at, isResolutionChange: false);
            var scored = tracked.ActiveTracks
                .Select(t => new ScoredTrack(t, t.Confidence, t.LastDetectionTimeUtc))
                .ToArray();
            fired.AddRange(engine.Evaluate(at, scored, "sim-detector/1"));
        }

        var only = Assert.Single(fired);
        Assert.Equal("zone-a", only.RuleId);
        Assert.Equal(1, only.RuleVersion);
        Assert.Equal("sim-detector/1", only.ModelVersion);
        // Three timestamps, recorded separately.
        Assert.Equal(T0.AddSeconds(5 / 5.0), only.EventTimeUtc);
        Assert.Equal(only.EventTimeUtc, only.DetectionTimeUtc);
        Assert.NotEmpty(only.FootTrailSnapshot);
    }

    // ---- full MP4 pipeline (requires the ffmpeg CLI + SkiaSharp natives: Plan 2 CI prerequisites) ----

    [Fact]
    public async Task Pipeline_WalkerMp4_DecodeTrackRule_FiresExactlyOneEvent()
    {
        // ffmpeg is a connected-CI prerequisite for Plan 2: without the CLI this
        // test passes vacuously with a note instead of failing the suite.
        if (!FfmpegAvailable())
        {
            Console.WriteLine("SKIP Pipeline_WalkerMp4: ffmpeg CLI not on PATH (Plan 2 CI prerequisite).");
            return;
        }

        var dir = NewTempDir();
        var script = SceneScript.Build("walker-crossing-zone", seed: 7, frameCount: 40);
        SceneRenderer.RenderAll(script, Path.Combine(dir, "png"));
        await File.WriteAllTextAsync(
            Path.Combine(dir, "manifest.json"), script.ToManifestJson() + "\n");

        var muxCode = await Mux.MakeMp4Async(script, Path.Combine(dir, "png"), Path.Combine(dir, "sim.mp4"));
        Assert.Equal(0, muxCode);

        var detector = ManifestReplayDetector.LoadFromFile(Path.Combine(dir, "manifest.json"));
        var engine = ZoneEngine(persistenceFrames: 3);
        var tracker = new Tracker(graceFrames: 5);
        var fired = new List<RuleEvent>();
        var downs = new List<string>();
        using var source = new FfmpegFrameSource(
            "cam-1", Path.Combine(dir, "sim.mp4"), 640, 480, 5.0, T0, onStreamDown: downs.Add);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var count = 0;
        byte[]? previous = null;
        await foreach (var frame in source.GetFramesAsync(cts.Token))
        {
            Assert.Equal(count, frame.Number);
            Assert.Equal(640, frame.Width);
            Assert.Equal(480, frame.Height);
            Assert.Equal(T0.AddSeconds(count / 5.0), frame.CapturedAtUtc);
            // Pixel path: every decoded frame carries its own Rgb24 row-major payload.
            Assert.Equal(640 * 480 * 3, frame.Pixels.Length);
            if (previous is not null)
            {
                Assert.False(frame.Pixels.Span.SequenceEqual(previous),
                    "Consecutive decoded frames must differ (walker moves).");
            }

            previous = frame.Pixels.ToArray();
            var detections = await detector.DetectAsync(frame, cts.Token);
            var tracked = tracker.Update(frame.Number, detections, frame.CapturedAtUtc, frame.IsResolutionChange);
            var scored = tracked.ActiveTracks
                .Select(t => new ScoredTrack(t, t.Confidence, t.LastDetectionTimeUtc))
                .ToArray();
            fired.AddRange(engine.Evaluate(frame.CapturedAtUtc, scored, "sim-detector/1", cts.Token));
            count++;
        }

        Assert.Equal(script.Frames.Count, count); // every encoded frame decoded
        Assert.Contains(downs, static r => r == "eof");
        var only = Assert.Single(fired);
        Assert.Equal("zone-a", only.RuleId);
        Assert.Equal(1, only.RuleVersion);
        Assert.Equal("sim-detector/1", only.ModelVersion);
    }

    private static RuleEngine ZoneEngine(int persistenceFrames)
    {
        // Start == End covers the whole day: always closed (armed), exercising the schedule path.
        var schedules = new Dictionary<string, Schedule>(StringComparer.Ordinal)
        {
            ["night"] = new Schedule("UTC", [new DailyWindow(new TimeOnly(0, 0), new TimeOnly(0, 0))]),
        };
        var rules = new[]
        {
            new RuleConfig(
                "zone-a", RuleType.ZoneIntrusion, version: 1,
                confidenceThreshold: 0.5, persistenceFrames: persistenceFrames,
                cooldownSeconds: 3600,
                polygon: new PolygonZone([
                    new TrackedPoint(0.35, 0.0), new TrackedPoint(0.65, 0.0),
                    new TrackedPoint(0.65, 1.0), new TrackedPoint(0.35, 1.0)]),
                scheduleId: "night"),
        };
        return new RuleEngine(rules, new ScheduleEvaluator(), schedules);
    }

    private static bool FfmpegAvailable()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            process.Start();
            process.WaitForExit(10_000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
