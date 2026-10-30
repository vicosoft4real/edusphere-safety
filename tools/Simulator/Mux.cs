using System.Diagnostics;

namespace EduSphere.Simulator;

/// <summary>
/// MP4 muxing in C#: stages the present PNGs under sequential names, so dropout gaps
/// DO survive as a shorter clip (mux renumbers: decoded frame N is the Nth present
/// PNG, not source index N), and the order-mapped replay detector keeps detection
/// attribution across that renumbering. Spawns FFmpeg with the pinned argv shape
/// (<c>-framerate N -i f%04d.png -c:v libx264 -pix_fmt yuv420p -movflags +faststart</c>,
/// plus <c>-y</c> so reruns never block on an overwrite prompt). No shell: tokens
/// go straight to <see cref="ProcessStartInfo.ArgumentList"/>.
/// </summary>
public static class Mux
{
    /// <summary>
    /// Encodes <paramref name="pngDirectory"/> (manifest frames) to
    /// <paramref name="outputMp4"/> via <paramref name="ffmpegPath"/>.
    /// Returns the FFmpeg exit code (0 = success).
    /// </summary>
    public static async Task<int> MakeMp4Async(
        SceneScript script,
        string pngDirectory,
        string outputMp4,
        string ffmpegPath = "ffmpeg",
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var stage = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())).FullName;
        try
        {
            var ordered = script.Frames.OrderBy(static f => f.FrameIndex).ToArray();
            for (var i = 0; i < ordered.Length; i++)
            {
                var src = Path.Combine(pngDirectory, SceneScript.FrameFileName(ordered[i].FrameIndex));
                File.Copy(src, Path.Combine(stage, $"f{i:D4}.png"));
            }

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-framerate");
            psi.ArgumentList.Add(script.Fps.ToString(System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(Path.Combine(stage, "f%04d.png"));
            psi.ArgumentList.Add("-c:v");
            psi.ArgumentList.Add("libx264");
            psi.ArgumentList.Add("-pix_fmt");
            psi.ArgumentList.Add("yuv420p");
            psi.ArgumentList.Add("-movflags");
            psi.ArgumentList.Add("+faststart");
            psi.ArgumentList.Add(outputMp4);

            using var process = new Process { StartInfo = psi };
            process.Start();
            var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                log?.Invoke(stderr.Trim());
            }

            return process.ExitCode;
        }
        finally
        {
            try
            {
                Directory.Delete(stage, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
