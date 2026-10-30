using System.Diagnostics;
using System.Net;
using System.Text;

namespace EduSphere.Safety.Edge;

/// <summary>Which URL shape an <see cref="FfmpegFrameSource"/> was built for.</summary>
public enum FrameSourceMode
{
    /// <summary>Local file input (<c>file://</c> URL or absolute path).</summary>
    File,

    /// <summary>Network camera input (<c>rtsp://</c> with a loopback/private-literal host).</summary>
    Rtsp,
}

/// <summary>
/// FFmpeg-backed <see cref="IFrameSource"/>: spawns the FFmpeg CLI with an argv-only
/// command line (no shell), decodes to <c>rawvideo rgb24</c> on stdout, and parses
/// fixed-size frames out of the byte stream.
/// <para>
/// URL ALLOWLIST (enforced in the constructor BEFORE any process is spawned):
/// <list type="bullet">
/// <item>File mode: a <c>file://</c> URL with an empty/<c>localhost</c> host and an
/// absolute path, or a bare absolute filesystem path.</item>
/// <item>RTSP mode: an <c>rtsp://</c> URL whose host is a loopback literal
/// (<c>localhost</c>, <c>127.0.0.0/8</c>, <c>::1</c>) or an RFC 1918 private literal
/// (<c>10/8</c>, <c>172.16/12</c>, <c>192.168/16</c>).</item>
/// <item>REJECTED: every other scheme (<c>concat:</c>, <c>pipe:</c>, <c>http(s):</c>,
/// <c>rtmp:</c>, <c>udp:</c>, …), <c>file://</c> with a remote host, relative paths,
/// link-local <c>169.254/16</c> (cloud metadata IPs live there), other public IPs,
/// and non-literal RTSP hostnames (DNS-gated; resolve-and-check is CI-only).</item>
/// </list>
/// </para>
/// <para>
/// CAPTURE TIMESTAMPS: this library never reads the wall clock. Frame
/// <c>n</c> is stamped <c>firstFrameTimeUtc + n / frameRate</c> from the
/// caller-supplied basis — an exact schedule for file input and a documented
/// approximation for live RTSP (detection/event times are still recorded
/// separately per event, and the outbox layer stamps ingest time itself).
/// </para>
/// <para>
/// HEALTH INTEGRATION (wired by the Agent host, not here): after each yielded frame
/// the host feeds <see cref="LastFrameHash"/> into
/// <c>FrameHealthMonitor.NoteFrame(hash, streamOpen: true, frame.CapturedAtUtc)</c>
/// (frozen-frame detection compares those hashes); the host calls
/// <c>NoteProcessExit</c> edge-triggered from <c>onStreamDown</c> exit reasons and
/// polls <c>QueryIdle</c> with its own clock. Resolution changes do NOT shift zones
/// (all geometry is normalized 0..1) but DO invalidate track identity: the RTSP
/// reconnect path calls <see cref="SignalResolutionChange"/> so the next frame
/// carries <c>IsResolutionChange</c> and downstream stages reset tracks.
/// </para>
/// </summary>
public sealed class FfmpegFrameSource : IFrameSource, IDisposable
{
    private readonly string _ffmpegPath;
    private readonly string _sourceUrl;
    private readonly string _transport;
    private readonly DateTimeOffset _firstFrameTimeUtc;
    private readonly Action<string>? _logStderr;
    private readonly Action<string>? _onStreamDown;
    private readonly int _frameBytes;
    private int _started;
    private int _resolutionChangePending;
    private Process? _process;
    private bool _disposed;

    /// <summary>
    /// Creates a frame source. The URL allowlist is enforced here, before spawn.
    /// </summary>
    /// <param name="cameraId">Stable camera id, stamped on every frame.</param>
    /// <param name="sourceUrl">File path/<c>file://</c> URL (file mode) or <c>rtsp://</c> URL (RTSP mode).</param>
    /// <param name="width">Decode width in pixels; must be positive.</param>
    /// <param name="height">Decode height in pixels; must be positive.</param>
    /// <param name="frameRate">Frames per second; must be finite and positive.</param>
    /// <param name="firstFrameTimeUtc">
    /// Caller-supplied event-time basis: frame <c>n</c> is stamped
    /// <c>firstFrameTimeUtc + n / frameRate</c>. Never read from the wall clock here.
    /// </param>
    /// <param name="ffmpegPath">FFmpeg binary; default <c>"ffmpeg"</c> (PATH lookup).</param>
    /// <param name="transport">RTSP transport, <c>"tcp"</c> (default) or <c>"udp"</c>; file mode ignores it.</param>
    /// <param name="logStderr">Caller sink for FFmpeg stderr lines (password-redacted). Null drops them.</param>
    /// <param name="onStreamDown">
    /// Caller callback invoked once per terminal condition with a reason string
    /// (<c>"eof"</c> on clean end-of-stream, <c>"ffmpeg-exit:{code}"</c> on crash,
    /// <c>"ffmpeg-truncated"</c> on a torn frame). Cancellation by the caller is NOT
    /// reported (caller-initiated, not a stream fault).
    /// </param>
    public FfmpegFrameSource(
        string cameraId,
        string sourceUrl,
        int width,
        int height,
        double frameRate,
        DateTimeOffset firstFrameTimeUtc,
        string ffmpegPath = "ffmpeg",
        string transport = "tcp",
        Action<string>? logStderr = null,
        Action<string>? onStreamDown = null)
    {
        if (string.IsNullOrWhiteSpace(cameraId))
        {
            throw new ArgumentException("Camera id must not be empty.", nameof(cameraId));
        }

        Mode = ValidateSourceUrl(sourceUrl);
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Decode width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Decode height must be positive.");
        }

        if (double.IsNaN(frameRate) || double.IsInfinity(frameRate) || frameRate <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameRate), "Frame rate must be finite and positive.");
        }

        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            throw new ArgumentException("FFmpeg path must not be empty.", nameof(ffmpegPath));
        }

        var normalizedTransport = transport?.Trim().ToLowerInvariant();
        if (normalizedTransport != "tcp" && normalizedTransport != "udp")
        {
            throw new ArgumentException("RTSP transport must be \"tcp\" or \"udp\".", nameof(transport));
        }

        CameraId = cameraId;
        _sourceUrl = sourceUrl;
        Width = width;
        Height = height;
        FrameRate = frameRate;
        _firstFrameTimeUtc = firstFrameTimeUtc;
        _ffmpegPath = ffmpegPath;
        _transport = normalizedTransport;
        _logStderr = logStderr;
        _onStreamDown = onStreamDown;
        _frameBytes = checked(width * height * 3);
    }

    /// <inheritdoc />
    public string CameraId { get; }

    /// <summary>File or RTSP, decided by the allowlist at construction.</summary>
    public FrameSourceMode Mode { get; }

    /// <summary>Decode width in pixels.</summary>
    public int Width { get; }

    /// <summary>Decode height in pixels.</summary>
    public int Height { get; }

    /// <summary>Frames per second.</summary>
    public double FrameRate { get; }

    /// <summary>
    /// FNV-1a hash of the most recently yielded frame's raw bytes, for the host to feed
    /// to <c>FrameHealthMonitor.NoteFrame</c>. Null before the first frame.
    /// </summary>
    public ulong? LastFrameHash { get; private set; }

    /// <summary>
    /// Marks the next yielded frame with <c>IsResolutionChange = true</c> (RTSP reconnect
    /// path: the stream geometry changed, so downstream stages must reset tracks).
    /// File mode has fixed geometry and never needs this. At-least-once: the flag is
    /// set on exactly the next frame, then cleared.
    /// </summary>
    public void SignalResolutionChange() => Interlocked.Exchange(ref _resolutionChangePending, 1);

    /// <summary>
    /// Enforces the URL allowlist and returns the source mode.
    /// Throws <see cref="ArgumentException"/> for anything else — including
    /// <c>concat:</c>/<c>pipe:</c> schemes and metadata IPs — before any spawn.
    /// </summary>
    /// <exception cref="ArgumentException">The URL is not allowlisted.</exception>
    public static FrameSourceMode ValidateSourceUrl(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            throw new ArgumentException("Source URL must not be empty.", nameof(sourceUrl));
        }

        var url = sourceUrl.Trim();

        // Bare absolute filesystem path → file mode. Relative paths are rejected:
        // the working directory at spawn time must never influence which file opens.
        if (!url.Contains("://", StringComparison.Ordinal))
        {
            if (Path.IsPathFullyQualified(url))
            {
                return FrameSourceMode.File;
            }

            throw new ArgumentException(
                "Bare source paths must be absolute; relative paths are rejected.", nameof(sourceUrl));
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("Source URL is not a valid absolute URI.", nameof(sourceUrl));
        }

        switch (uri.Scheme.ToLowerInvariant())
        {
            case "file":
                if (!string.IsNullOrEmpty(uri.Host) &&
                    !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        "file:// URLs with a remote host are rejected.", nameof(sourceUrl));
                }

                // file://localhost/… names this machine (RFC 8089): judge the URI
                // path, not the UNC-mapped LocalPath the runtime produces for it.
                var filePath = uri.Host.Length == 0
                    ? uri.LocalPath
                    : Uri.UnescapeDataString(uri.AbsolutePath);
                if (!Path.IsPathFullyQualified(filePath))
                {
                    throw new ArgumentException(
                        "file:// URLs must carry an absolute path.", nameof(sourceUrl));
                }

                return FrameSourceMode.File;

            case "rtsp":
                if (IsLoopbackOrPrivateLiteral(uri.Host))
                {
                    return FrameSourceMode.Rtsp;
                }

                throw new ArgumentException(
                    "rtsp:// hosts must be loopback/localhost literals or RFC 1918 private IP " +
                    "literals (10/8, 172.16/12, 192.168/16); public IPs, link-local " +
                    "169.254/16 (metadata range), and DNS hostnames are rejected.",
                    nameof(sourceUrl));

            default:
                // concat:, pipe:, http(s):, rtmp:, udp:, rtp:, srt:, … — none are spawnable here.
                throw new ArgumentException(
                    $"URL scheme \"{uri.Scheme}\" is not allowlisted (file or rtsp only).",
                    nameof(sourceUrl));
        }
    }

    /// <summary>
    /// Pure argv builder (no process): the exact <see cref="ProcessStartInfo.ArgumentList"/>
    /// contents for <paramref name="mode"/>. File mode replays in real time
    /// (<c>-re</c>); RTSP mode pins the transport (<c>-rtsp_transport tcp</c>) before
    /// <c>-i</c>. Both modes pass <c>-protocol_whitelist</c> immediately before
    /// <c>-i</c> (<c>file</c> for file mode, <c>rtsp,rtp,tcp,udp</c> for RTSP mode)
    /// so a redirected or playlist-wrapped input cannot pull FFmpeg into a
    /// non-allowlisted protocol. Never a shell string — the caller passes these
    /// tokens straight to <c>ArgumentList</c> so URLs cannot inject flags.
    /// <para>
    /// ARGV CREDENTIAL EXPOSURE (Plan 4 note — no rotation here): the RTSP URL,
    /// userinfo included, travels as cleartext argv tokens, visible to local process
    /// listing (<c>ps</c>) while FFmpeg runs. Mitigations live with deployment, not
    /// this library: least-privilege camera accounts (streaming-only, never admin),
    /// short-lived credentials rotated by the Plan 4 host, and logs carrying only
    /// <see cref="RedactCredentials"/> output. This library never persists the URL
    /// beyond the child lifetime.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> BuildArgumentList(
        FrameSourceMode mode,
        string sourcePathOrUrl,
        int width,
        int height,
        double frameRate,
        string transport = "tcp")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePathOrUrl);
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
        };
        if (mode == FrameSourceMode.File)
        {
            args.Add("-re");
        }
        else
        {
            args.Add("-rtsp_transport");
            args.Add(transport);
        }

        args.Add("-protocol_whitelist");
        args.Add(mode == FrameSourceMode.File ? "file" : "rtsp,rtp,tcp,udp");

        args.Add("-i");
        args.Add(mode == FrameSourceMode.File ? ToLocalPath(sourcePathOrUrl) : sourcePathOrUrl);
        args.Add("-f");
        args.Add("rawvideo");
        args.Add("-pix_fmt");
        args.Add("rgb24");
        args.Add("-s");
        args.Add($"{width}x{height}");
        args.Add("-r");
        args.Add(frameRate.ToString("G17", System.Globalization.CultureInfo.InvariantCulture));
        args.Add("-");
        return args;
    }

    /// <summary>
    /// Masks EVERY URL-embedded password on the line (<c>://user:secret@</c> →
    /// <c>://user:***@</c>, looping over all spans) for the stderr log sink.
    /// Applied to every drained line; the raw URL is never logged. Only the authority
    /// token is scanned (the span ends at <c>/</c>, whitespace, or end of line), so a
    /// bare <c>user@host</c> (no password) and non-URL text pass through untouched.
    /// </summary>
    public static string RedactCredentials(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return string.Empty;
        }

        var redacted = new StringBuilder(line.Length);
        var offset = 0;
        while (offset < line.Length)
        {
            var schemeEnd = line.IndexOf("://", offset, StringComparison.Ordinal);
            if (schemeEnd < 0)
            {
                break;
            }

            // Scan one authority token: userinfo ends at '@', the token at '/'.
            var at = -1;
            var colon = -1;
            var scan = schemeEnd + 3;
            while (scan < line.Length)
            {
                var c = line[scan];
                if (c == '@')
                {
                    at = scan;
                    break;
                }

                if (c == '/' || char.IsWhiteSpace(c))
                {
                    break;
                }

                if (c == ':' && colon < 0)
                {
                    colon = scan;
                }

                scan++;
            }

            if (at < 0 || colon < 0)
            {
                if (scan >= line.Length)
                {
                    break; // Nothing redactable left on the line.
                }

                // Not a credential span (bare userinfo or bare host): keep it
                // verbatim so log text is never dropped; later spans still masked.
                redacted.Append(line.AsSpan(offset, scan + 1 - offset));
                offset = scan + 1; // Skip this token; later spans still get masked.
                continue;
            }

            redacted.Append(line.AsSpan(offset, colon - offset + 1));
            redacted.Append("***");
            offset = at;
        }

        redacted.Append(line.AsSpan(offset));
        return redacted.ToString();
    }

    /// <summary>
    /// 64-bit FNV-1a hash of raw frame bytes — the frozen-frame signature fed to
    /// <c>FrameHealthMonitor.NoteFrame</c> (identical-frame streaks mean a frozen stream).
    /// </summary>
    public static ulong FrameHash(ReadOnlySpan<byte> rawRgb24)
    {
        const ulong offset = 14695981039346656037ul;
        const ulong prime = 1099511628211ul;
        var hash = offset;
        foreach (var b in rawRgb24)
        {
            hash ^= b;
            hash *= prime;
        }

        return hash;
    }

    /// <summary>
    /// Reads exactly <c>frameBuffer.Length</c> bytes from <paramref name="stdout"/>.
    /// Returns false on clean end-of-stream (zero bytes at a frame boundary);
    /// throws <see cref="IOException"/> on a torn mid-frame read (crashed FFmpeg).
    /// </summary>
    public static bool TryReadFrame(Stream stdout, byte[] frameBuffer)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(frameBuffer);
        var offset = 0;
        while (offset < frameBuffer.Length)
        {
            var read = stdout.Read(frameBuffer, offset, frameBuffer.Length - offset);
            if (read == 0)
            {
                if (offset == 0)
                {
                    return false; // Clean EOF on a frame boundary.
                }

                throw new IOException(
                    $"FFmpeg stdout ended mid-frame ({offset}/{frameBuffer.Length} bytes).");
            }

            offset += read;
        }

        return true;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Frame> GetFramesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("This source has already been started.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false, // Never a shell: ArgumentList tokens pass through verbatim.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
        };
        foreach (var token in BuildArgumentList(Mode, _sourceUrl, Width, Height, FrameRate, _transport))
        {
            psi.ArgumentList.Add(token);
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process = process;
        try
        {
            if (!process.Start())
            {
                _onStreamDown?.Invoke("ffmpeg-start-failed");
                _process = null;
                process.Dispose();
                yield break;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            _onStreamDown?.Invoke($"ffmpeg-start-failed: {ex.GetType().Name}");
            _process = null;
            process.Dispose();
            yield break;
        }

        // Drain stderr without blocking the frame loop; every line is redacted first.
        var stderrTask = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                {
                    _logStderr?.Invoke(RedactCredentials(line));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
        }, CancellationToken.None);

        var buffer = new byte[_frameBytes];
        long number = 0;
        var streamDownReported = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool gotFrame;
                try
                {
                    // Stdout is a pipe; read it on a worker so cancellation kills the loop
                    // instead of hanging inside a blocking read.
                    gotFrame = await Task.Run(() => TryReadFrame(process.StandardOutput.BaseStream, buffer), ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException ex)
                {
                    _onStreamDown?.Invoke($"ffmpeg-truncated: {ex.Message}");
                    streamDownReported = true;
                    break;
                }

                if (!gotFrame)
                {
                    break; // Clean EOF; exit code below decides the reason string.
                }

                LastFrameHash = FrameHash(buffer);
                // The scratch buffer is reused across frames, so each yielded frame takes
                // home a private copy as its Rgb24 row-major payload (still hashed above
                // for the frozen-frame health path). Never hand over the scratch buffer:
                // the next read would silently rewrite already-yielded frames.
                var owned = buffer.ToArray();
                var resolutionChange = Interlocked.Exchange(ref _resolutionChangePending, 0) != 0;
                var capturedAt = _firstFrameTimeUtc.AddSeconds(number / FrameRate);
                yield return new Frame(CameraId, number, capturedAt, Width, Height, resolutionChange, owned);
                number++;
            }
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }

            string? reason = null;
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                if (!streamDownReported && !ct.IsCancellationRequested)
                {
                    reason = process.ExitCode == 0
                        ? "eof"
                        : $"ffmpeg-exit:{process.ExitCode}";
                }
            }
            catch (OperationCanceledException)
            {
            }

            try
            {
                await stderrTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            if (reason is not null)
            {
                _onStreamDown?.Invoke(reason);
            }

            process.Dispose();
            _process = null;
        }
    }

    /// <summary>Stops a running FFmpeg child (if any). Never throws; safe to call twice.</summary>
    /// <remarks>
    /// Dispose → Kill → pipe-close unblocking: killing the child closes its stdout
    /// pipe, which unblocks a concurrent <c>GetFramesAsync</c> reader stuck in
    /// <c>TryReadFrame</c> so its cleanup path (<c>WaitForExitAsync</c> + stderr
    /// drain + <c>process.Dispose</c>) can run to completion instead of hanging on
    /// a dead pipe. The wait below is bounded (5 s): Dispose must never hang the
    /// host when the child ignores the kill.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var process = Interlocked.Exchange(ref _process, null);
        try
        {
            process?.Kill(entireProcessTree: true);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        try
        {
            process?.WaitForExit(5000);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        try
        {
            process?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static bool IsLoopbackOrPrivateLiteral(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var trimmed = host.Trim().Trim('[', ']');
        if (!IPAddress.TryParse(trimmed, out var address))
        {
            return false; // Non-literal hostname: DNS-gated, rejected here.
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            // 10/8
            if (bytes[0] == 10)
            {
                return true;
            }

            // 172.16/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            {
                return true;
            }

            // 192.168/16
            if (bytes[0] == 192 && bytes[1] == 168)
            {
                return true;
            }
        }

        return false;
    }

    private static string ToLocalPath(string sourcePathOrUrl)
    {
        if (Uri.TryCreate(sourcePathOrUrl, UriKind.Absolute, out var uri) &&
            uri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            return uri.LocalPath;
        }

        return sourcePathOrUrl;
    }
}
