using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EduSphere.Safety.Edge;

/// <summary>Outcome of one upload attempt (no sockets in tests: HttpClient runs over a fake handler).</summary>
public enum UploadResult
{
    /// <summary>Cloud accepted the event (2xx).</summary>
    Uploaded = 0,

    /// <summary>429 / 5xx / network error: keep queued, back off, retry.</summary>
    Retryable = 1,

    /// <summary>401/403: device credentials rejected — do not retry blindly, surface to ops.</summary>
    FatalAuth = 2,

    /// <summary>Other 4xx: cloud refused the payload — do not retry, quarantine for review.</summary>
    FatalRejected = 3,
}

/// <summary>
/// One event upload: metadata plus optional clip/snapshot bytes.
/// The stable <see cref="EdgeEventId"/> lets the cloud deduplicate retries/idempotent replays.
/// </summary>
public sealed record EventUploadPayload(
    string EdgeEventId,
    string DeviceId,
    string RuleId,
    int RuleVersion,
    long TrackId,
    DateTimeOffset EventTimeUtc,
    DateTimeOffset DetectionTimeUtc,
    DateTimeOffset IngestTimeUtc,
    string? SegmentId,
    byte[]? ClipBytes,
    byte[]? SnapshotBytes);

/// <summary>
/// Uploads events as JSON with base64 clip/snapshot bytes (chosen over multipart
/// so the whole body stays one JSON document the fake-handler tests can assert on).
/// BOUND: <see cref="MaxClipBytes"/> (2 MiB) per clip — larger clips are rejected
/// client-side with <see cref="ArgumentException"/> before any byte is sent; the
/// caller must reference the sealed buffer segment instead and let the clip follow
/// out-of-band. Snapshot bytes share the same bound.
/// <para>
/// Backoff with FULL JITTER: <c>sleep = uniform(0, min(cap, base * 2^attempt))</c>,
/// base 1 s, cap 60 s. Jitter comes from a <see cref="Random"/> supplied via the
/// constructor (pass a fixed seed in tests for determinism).
/// </para>
/// </summary>
public sealed class UploadClient
{
    /// <summary>Maximum clip (or snapshot) bytes per upload; larger is rejected client-side.</summary>
    public const long MaxClipBytes = 2L * 1024 * 1024;

    private const string DeviceKeyHeader = "X-Device-Key";
    private const double BaseDelaySeconds = 1.0;
    private const double MaxDelaySeconds = 60.0;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http;
    private readonly string _deviceId;
    private readonly string _secret;
    private readonly Random _rng;

    /// <summary>Creates an upload client. The <see cref="HttpClient"/> must have BaseAddress set.</summary>
    public UploadClient(HttpClient http, string deviceId, string secret, Random? rng = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("Device id must not be empty.", nameof(deviceId));
        }

        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ArgumentException("Device secret must not be empty.", nameof(secret));
        }

        _deviceId = deviceId;
        _secret = secret;
        _rng = rng ?? new Random();
    }

    /// <summary>Full-jitter backoff for a 0-based attempt number.</summary>
    public TimeSpan BackoffDelay(int attempt)
    {
        if (attempt < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt), "Attempt must be >= 0.");
        }

        var cap = Math.Min(MaxDelaySeconds, BaseDelaySeconds * Math.Pow(2, attempt));
        lock (_rng)
        {
            return TimeSpan.FromSeconds(_rng.NextDouble() * cap);
        }
    }

    /// <summary>Single upload attempt; maps status codes to <see cref="UploadResult"/>.</summary>
    /// <exception cref="ArgumentException">Clip/snapshot exceeds <see cref="MaxClipBytes"/>.</exception>
    public async Task<UploadResult> UploadAsync(EventUploadPayload payload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.ClipBytes is not null && payload.ClipBytes.Length > MaxClipBytes)
        {
            throw new ArgumentException($"Clip exceeds {MaxClipBytes} bytes.", nameof(payload));
        }

        if (payload.SnapshotBytes is not null && payload.SnapshotBytes.Length > MaxClipBytes)
        {
            throw new ArgumentException($"Snapshot exceeds {MaxClipBytes} bytes.", nameof(payload));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "edge/events");
        request.Headers.Add(DeviceKeyHeader, $"{_deviceId}.{_secret}");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new UploadBody(payload), Json),
            System.Text.Encoding.UTF8,
            "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return UploadResult.Retryable;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return UploadResult.Retryable; // Timeout, not caller cancellation.
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (status is >= 200 and < 300)
            {
                return UploadResult.Uploaded;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests || status >= 500)
            {
                return UploadResult.Retryable;
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return UploadResult.FatalAuth;
            }

            return UploadResult.FatalRejected;
        }
    }

    /// <summary>
    /// Uploads with retries: retryable results are retried up to
    /// <paramref name="maxAttempts"/> total attempts, sleeping
    /// <paramref name="delayForAttempt"/> (or <see cref="BackoffDelay"/>) between them.
    /// Fatal results return immediately without further attempts.
    /// </summary>
    public async Task<UploadResult> UploadWithRetryAsync(
        EventUploadPayload payload,
        int maxAttempts,
        Func<int, TimeSpan>? delayForAttempt = null,
        CancellationToken ct = default)
    {
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "Need at least 1 attempt.");
        }

        for (var attempt = 0; ; attempt++)
        {
            var result = await UploadAsync(payload, ct).ConfigureAwait(false);
            if (result != UploadResult.Retryable || attempt + 1 >= maxAttempts)
            {
                return result;
            }

            var delay = delayForAttempt is not null ? delayForAttempt(attempt) : BackoffDelay(attempt);
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    private sealed class UploadBody
    {
        public UploadBody(EventUploadPayload p)
        {
            EdgeEventId = p.EdgeEventId;
            DeviceId = p.DeviceId;
            RuleId = p.RuleId;
            RuleVersion = p.RuleVersion;
            TrackId = p.TrackId;
            EventTimeUtc = p.EventTimeUtc;
            DetectionTimeUtc = p.DetectionTimeUtc;
            IngestTimeUtc = p.IngestTimeUtc;
            SegmentId = p.SegmentId;
            ClipBase64 = p.ClipBytes is null ? null : Convert.ToBase64String(p.ClipBytes);
            SnapshotBase64 = p.SnapshotBytes is null ? null : Convert.ToBase64String(p.SnapshotBytes);
        }

        public string EdgeEventId { get; }
        public string DeviceId { get; }
        public string RuleId { get; }
        public int RuleVersion { get; }
        public long TrackId { get; }
        public DateTimeOffset EventTimeUtc { get; }
        public DateTimeOffset DetectionTimeUtc { get; }
        public DateTimeOffset IngestTimeUtc { get; }
        public string? SegmentId { get; }
        public string? ClipBase64 { get; }
        public string? SnapshotBase64 { get; }
    }
}

/// <summary>Heartbeat payload: identity, versions, and edge health signals.</summary>
public sealed record HeartbeatPayload(
    string DeviceId,
    string SoftwareVersion,
    string DetectorVersion,
    int ConfigVersion,
    int QueueDepth,
    double FrameAgeSeconds,
    long DiskUsedBytes);

/// <summary>Builds and posts heartbeat payloads (POST edge/heartbeat).</summary>
public sealed class HeartbeatClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http;
    private readonly string _deviceId;

    /// <summary>Creates a heartbeat client. The <see cref="HttpClient"/> must have BaseAddress set.</summary>
    public HeartbeatClient(HttpClient http, string deviceId)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("Device id must not be empty.", nameof(deviceId));
        }

        _deviceId = deviceId;
    }

    /// <summary>Pure payload builder (versions + queue depth + frame age + disk use).</summary>
    /// <exception cref="ArgumentException">Negative queue/disk or non-finite frame age.</exception>
    public HeartbeatPayload Build(
        string softwareVersion,
        string detectorVersion,
        int configVersion,
        int queueDepth,
        double frameAgeSeconds,
        long diskUsedBytes)
    {
        if (string.IsNullOrWhiteSpace(softwareVersion))
        {
            throw new ArgumentException("Software version must not be empty.", nameof(softwareVersion));
        }

        if (string.IsNullOrWhiteSpace(detectorVersion))
        {
            throw new ArgumentException("Detector version must not be empty.", nameof(detectorVersion));
        }

        if (queueDepth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(queueDepth), "Queue depth must be >= 0.");
        }

        if (double.IsNaN(frameAgeSeconds) || double.IsInfinity(frameAgeSeconds) || frameAgeSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameAgeSeconds), "Frame age must be finite and >= 0.");
        }

        if (diskUsedBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(diskUsedBytes), "Disk use must be >= 0.");
        }

        return new(
            _deviceId, softwareVersion, detectorVersion, configVersion,
            queueDepth, frameAgeSeconds, diskUsedBytes);
    }

    /// <summary>Posts one heartbeat; returns the status code for the caller to map.</summary>
    public async Task<HttpStatusCode> SendAsync(HeartbeatPayload payload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, "edge/heartbeat");
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, Json),
            System.Text.Encoding.UTF8,
            "application/json");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        return response.StatusCode;
    }
}

/// <summary>
/// Pulls the versioned rule bundle (GET edge/config, JSON
/// <c>{"version": N, "bundle": "..."}</c>). Staleness is a caller-side comparison:
/// <see cref="IsStale"/> flags ANY difference between server and local version
/// (server ahead OR behind — both mean the edge is not running the served config;
/// the heartbeat echoes the local version so the cloud can see it too).
/// </summary>
public sealed class ConfigClient
{
    private readonly HttpClient _http;
    private readonly string _deviceId;

    /// <summary>Creates a config client. The <see cref="HttpClient"/> must have BaseAddress set.</summary>
    public ConfigClient(HttpClient http, string deviceId)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("Device id must not be empty.", nameof(deviceId));
        }

        _deviceId = deviceId;
    }

    /// <summary>True when the server version differs from the local version (either direction).</summary>
    public static bool IsStale(int localVersion, int serverVersion) => serverVersion != localVersion;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Pulls the bundle; returns the version plus the opaque bundle string.</summary>
    /// <remarks>
    /// Non-2xx surfaces as <see cref="HttpRequestException"/> via
    /// <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/> — callers treat that as
    /// retryable (same class as network errors), never fatal.
    /// </remarks>
    /// <exception cref="InvalidDataException">Body is not the expected versioned JSON.</exception>
    public async Task<(int Version, string Bundle)> PullAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("edge/config", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ConfigBody? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ConfigBody>(body, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Config body is not valid JSON.", ex);
        }

        if (parsed is null || parsed.Bundle is null)
        {
            throw new InvalidDataException("Config body lacks version/bundle.");
        }

        return (parsed.Version, parsed.Bundle);
    }

    private sealed class ConfigBody
    {
        public int Version { get; set; }
        public string? Bundle { get; set; }
    }
}

/// <summary>Well-known health reasons for <see cref="HealthEvent"/>.</summary>
public static class HealthReasons
{
    /// <summary>N identical frame signatures with the stream open (frozen frame).</summary>
    public const string Frozen = "frozen";

    /// <summary>No frame for longer than the stream-lost timeout (disconnect).</summary>
    public const string StreamLost = "stream-lost";

    /// <summary>FFmpeg/analysis process exited non-zero.</summary>
    public const string AnalysisFailure = "analysis-failure";
}

/// <summary>
/// Type-E health event, same shape family as <see cref="RuleEvent"/>:
/// rule id "health", version 1, plus event time and reason.
/// </summary>
public sealed record HealthEvent(string Reason, DateTimeOffset EventTimeUtc)
{
    /// <summary>Always "E" (health events are produced by the ingest layer, not the rule engine).</summary>
    public string Type => "E";

    /// <summary>Always "health".</summary>
    public string RuleId => "health";

    /// <summary>Always 1.</summary>
    public int RuleVersion => 1;
}

/// <summary>
/// Ingest-layer health hooks. Pure apart from the caller-supplied emitter:
/// every timestamp is passed in, no clock is read. Feed each decoded frame via
/// <see cref="NoteFrame"/> with a caller-supplied signature (e.g. a hash of the
/// raw bytes) plus whether the stream is open; poll <see cref="QueryIdle"/> with
/// an explicit query time; signal FFmpeg exit via <see cref="NoteProcessExit"/>.
/// <list type="bullet">
/// <item>Frozen: <c>frozenThreshold</c> identical signatures in a row WITH the
/// stream open emits once per streak (a changed signature re-arms).</item>
/// <item>StreamLost: no frame for LONGER than <c>streamLostTimeout</c> emits once
/// (a new frame re-arms). Exactly-at-timeout is not lost.</item>
/// <item>AnalysisFailure: a non-zero exit code emits immediately; zero is silent.</item>
/// </list>
/// </summary>
public sealed class FrameHealthMonitor
{
    private readonly Action<HealthEvent> _emit;
    private readonly int _frozenThreshold;
    private readonly TimeSpan _streamLostTimeout;
    private readonly object _gate = new();
    private ulong? _lastSignature;
    private int _identicalCount;
    private bool _frozenEmitted;
    private DateTimeOffset? _lastFrameTime;
    private bool _streamLostEmitted;

    /// <summary>Creates a monitor emitting E-events through <paramref name="emit"/>.</summary>
    public FrameHealthMonitor(
        Action<HealthEvent> emit,
        int frozenThreshold = 30,
        TimeSpan? streamLostTimeout = null)
    {
        _emit = emit ?? throw new ArgumentNullException(nameof(emit));
        if (frozenThreshold < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(frozenThreshold), "Frozen threshold must be >= 2.");
        }

        var timeout = streamLostTimeout ?? TimeSpan.FromSeconds(10);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(streamLostTimeout), "Timeout must be positive.");
        }

        _frozenThreshold = frozenThreshold;
        _streamLostTimeout = timeout;
    }

    /// <summary>
    /// Feeds one frame. Thread-safe. When the stream is down, STOP calling and rely on
    /// <see cref="QueryIdle"/>: a call with <paramref name="streamOpen"/> false does NOT
    /// refresh the idle clock (it only resets frozen streak state), so a down stream is
    /// still reported lost after the timeout instead of being suppressed forever.
    /// </summary>
    public void NoteFrame(ulong signature, bool streamOpen, DateTimeOffset eventTimeUtc)
    {
        lock (_gate)
        {
            if (!streamOpen)
            {
                _identicalCount = 0;
                _frozenEmitted = false;
                return;
            }

            _lastFrameTime = eventTimeUtc;
            _streamLostEmitted = false;
            if (_lastSignature == signature)
            {
                _identicalCount++;
            }
            else
            {
                _identicalCount = 1;
                _frozenEmitted = false;
            }

            _lastSignature = signature;
            if (_identicalCount >= _frozenThreshold && !_frozenEmitted)
            {
                _frozenEmitted = true;
                _emit(new HealthEvent(HealthReasons.Frozen, eventTimeUtc));
            }
        }
    }

    /// <summary>Emits stream-lost when the gap since the last frame exceeds the timeout. Thread-safe.</summary>
    public void QueryIdle(DateTimeOffset queryTimeUtc)
    {
        lock (_gate)
        {
            if (_lastFrameTime is null || _streamLostEmitted)
            {
                return;
            }

            if (queryTimeUtc - _lastFrameTime > _streamLostTimeout)
            {
                _streamLostEmitted = true;
                _emit(new HealthEvent(HealthReasons.StreamLost, queryTimeUtc));
            }
        }
    }

    /// <summary>
    /// Signals FFmpeg/analysis process exit; non-zero emits a failure event. Thread-safe.
    /// Edge-triggered by contract: the caller must call once per exit (repeated calls re-emit).
    /// </summary>
    public void NoteProcessExit(int exitCode, DateTimeOffset eventTimeUtc)
    {
        if (exitCode == 0)
        {
            return;
        }

        lock (_gate)
        {
            _emit(new HealthEvent(HealthReasons.AnalysisFailure, eventTimeUtc));
        }
    }
}
