using EduSphere.Simulator;

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

static bool HasFlag(string[] args, string name) => args.Contains(name);

if (HasFlag(args, "--list"))
{
    Console.WriteLine(string.Join("\n", SceneScript.Scenarios));
    return 0;
}

var scenario = Arg(args, "--scenario", "walker-crossing-zone");
var outDir = Arg(args, "--out", "sim-out");
var seed = ArgInt(args, "--seed", 7);
var frames = ArgInt(args, "--frames", 40);
var fps = ArgInt(args, "--fps", 5);
var width = ArgInt(args, "--width", 640);
var height = ArgInt(args, "--height", 480);
var ffmpeg = Arg(args, "--ffmpeg", "ffmpeg");
var skipMux = HasFlag(args, "--skip-mux");

if (!SceneScript.Scenarios.Contains(scenario))
{
    Console.Error.WriteLine($"Unknown scenario: {scenario} (see --list).");
    return 2;
}

var script = SceneScript.Build(scenario, seed, frames, width, height, fps);
var pngDir = Path.Combine(outDir, "png");
SceneRenderer.RenderAll(script, pngDir);

var manifestPath = Path.Combine(outDir, "manifest.json");
await File.WriteAllTextAsync(manifestPath, script.ToManifestJson() + "\n");
Console.WriteLine($"scenario={scenario} seed={seed} frames={script.Frames.Count} manifest={manifestPath}");
Console.WriteLine($"manifest-sha256={script.ManifestHash()}");

if (!skipMux)
{
    var mp4Path = Path.Combine(outDir, "sim.mp4");
    var code = await Mux.MakeMp4Async(script, pngDir, mp4Path, ffmpeg, Console.Error.WriteLine);
    Console.WriteLine($"mux exit={code} mp4={mp4Path}");
    return code == 0 ? 0 : code;
}

return 0;
