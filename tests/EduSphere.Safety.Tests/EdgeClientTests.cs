using System.Net;
using System.Text.Json;
using EduSphere.Safety.Edge;
using Xunit;

public sealed class EdgeClientTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 4, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeHandler : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = new();
        public readonly List<string> Bodies = new();
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();
        private readonly Queue<Exception> _errors = new();

        public void QueueStatus(HttpStatusCode status) =>
            _responses.Enqueue(() => new HttpResponseMessage(status));

        public void QueueError(Exception ex) => _errors.Enqueue(ex);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null
                ? string.Empty
                : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            if (_errors.Count > 0)
            {
                return Task.FromException<HttpResponseMessage>(_errors.Dequeue());
            }

            return Task.FromResult(_responses.Count > 0
                ? _responses.Dequeue()()
                : new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static (UploadClient Client, FakeHandler Handler) MakeUploadClient(int seed = 7) =>
        MakeUploadClient(new FakeHandler(), seed);

    private static (UploadClient Client, FakeHandler Handler) MakeUploadClient(FakeHandler handler, int seed = 7)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        return (new UploadClient(http, "cam-01", "s3cr3t", new Random(seed)), handler);
    }

    private static EventUploadPayload Payload(string edgeId = "edge-1") => new(
        edgeId, "cam-01", "zone-a", 2, 42,
        T0, T0.AddSeconds(-1), T0,
        "seg-00000001", new byte[] { 1, 2, 3 }, new byte[] { 4, 5 });

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    public async Task Upload_MapsSuccessAndRetryable(HttpStatusCode status)
    {
        var (client, handler) = MakeUploadClient();
        handler.QueueStatus(status);
        var expected = status == HttpStatusCode.OK || status == HttpStatusCode.Created
            ? UploadResult.Uploaded
            : UploadResult.Retryable;
        Assert.Equal(expected, await client.UploadAsync(Payload()));
    }

    [Fact]
    public async Task Upload_NetworkError_IsRetryable()
    {
        var handler = new FakeHandler();
        handler.QueueError(new HttpRequestException("boom"));
        var (client, _) = MakeUploadClient(handler);
        Assert.Equal(UploadResult.Retryable, await client.UploadAsync(Payload()));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, UploadResult.FatalAuth)]
    [InlineData(HttpStatusCode.Forbidden, UploadResult.FatalAuth)]
    [InlineData(HttpStatusCode.BadRequest, UploadResult.FatalRejected)]
    [InlineData(HttpStatusCode.NotFound, UploadResult.FatalRejected)]
    [InlineData(HttpStatusCode.UnprocessableEntity, UploadResult.FatalRejected)]
    public async Task Upload_MapsFatal(HttpStatusCode status, UploadResult expected)
    {
        var h = new FakeHandler();
        h.QueueStatus(status);
        var (client, _) = MakeUploadClient(h);
        Assert.Equal(expected, await client.UploadAsync(Payload()));
    }

    [Fact]
    public async Task Upload_SendsDeviceKeyHeader_AndStableEdgeId()
    {
        var (client, handler) = MakeUploadClient();
        Assert.Equal(UploadResult.Uploaded, await client.UploadAsync(Payload("edge-stable-9")));
        var request = Assert.Single(handler.Requests);
        Assert.Equal("cam-01.s3cr3t", request.Headers.GetValues("X-Device-Key").Single());
        var body = Assert.Single(handler.Bodies);
        Assert.Contains("edge-stable-9", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_ClipOverBound_ThrowsBeforeSend()
    {
        var (client, handler) = MakeUploadClient();
        var big = new EventUploadPayload(
            "edge-big", "cam-01", "zone-a", 2, 42, T0, T0, T0, null,
            new byte[UploadClient.MaxClipBytes + 1], null);
        await Assert.ThrowsAsync<ArgumentException>(() => client.UploadAsync(big));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Upload_RetryThenSuccess_StopsRetrying()
    {
        var handler = new FakeHandler();
        handler.QueueStatus(HttpStatusCode.ServiceUnavailable);
        handler.QueueStatus(HttpStatusCode.OK);
        var (client, _) = MakeUploadClient(handler);
        var result = await client.UploadWithRetryAsync(Payload(), maxAttempts: 3, _ => TimeSpan.Zero);
        Assert.Equal(UploadResult.Uploaded, result);
        Assert.True(handler.Requests.Count == 2, "Expected exactly 2 attempts.");
    }

    [Fact]
    public async Task Upload_Fatal_DoesNotRetry()
    {
        var handler = new FakeHandler();
        handler.QueueStatus(HttpStatusCode.Unauthorized);
        var (client, _) = MakeUploadClient(handler);
        var result = await client.UploadWithRetryAsync(Payload(), maxAttempts: 3, _ => TimeSpan.Zero);
        Assert.Equal(UploadResult.FatalAuth, result);
        Assert.True(handler.Requests.Count == 1, "Fatal results must not retry.");
    }

    [Fact]
    public void Backoff_SameSeed_SameSequence_AndCapped()
    {
        var (a, _) = MakeUploadClient(seed: 123);
        var (b, _) = MakeUploadClient(seed: 123);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            Assert.Equal(a.BackoffDelay(attempt), b.BackoffDelay(attempt));
        }

        // Attempt 100 at base 1s / cap 60s must still be within the cap.
        Assert.True(a.BackoffDelay(100) <= TimeSpan.FromSeconds(60));
        Assert.True(a.BackoffDelay(0) >= TimeSpan.Zero);
    }

    [Fact]
    public async Task Heartbeat_Payload_ContainsAllFields()
    {
        var handler = new FakeHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var hb = new HeartbeatClient(http, "cam-01");
        var payload = hb.Build("sw-1.2.3", "det-4.5", 9, 17, 2.5, 4096);
        Assert.Equal(HttpStatusCode.OK, await hb.SendAsync(payload));
        var body = Assert.Single(handler.Bodies);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("cam-01", root.GetProperty("deviceId").GetString());
        Assert.Equal("sw-1.2.3", root.GetProperty("softwareVersion").GetString());
        Assert.Equal("det-4.5", root.GetProperty("detectorVersion").GetString());
        Assert.Equal(9, root.GetProperty("configVersion").GetInt32());
        Assert.Equal(17, root.GetProperty("queueDepth").GetInt32());
        Assert.Equal(2.5, root.GetProperty("frameAgeSeconds").GetDouble());
        Assert.Equal(4096, root.GetProperty("diskUsedBytes").GetInt64());
    }

    [Fact]
    public async Task Config_Pull_ReturnsVersionAndBundle()
    {
        var h2 = new ConfigHandler("{\"version\": 7, \"bundle\": \"rules-yaml\"}");
        var http2 = new HttpClient(h2) { BaseAddress = new Uri("http://localhost/") };
        var config2 = new ConfigClient(http2, "cam-01");
        var (version, bundle) = await config2.PullAsync();
        Assert.Equal(7, version);
        Assert.Equal("rules-yaml", bundle);
    }

    private sealed class ConfigHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });
    }

    [Fact]
    public void Config_IsStale_FlagsAnyDifference()
    {
        Assert.False(ConfigClient.IsStale(7, 7));
        Assert.True(ConfigClient.IsStale(7, 8));
        Assert.True(ConfigClient.IsStale(8, 7));
    }

    [Fact]
    public void Health_FrozenAfterNIdentical_NMinusOneSilent()
    {
        var emitted = new List<HealthEvent>();
        var monitor = new FrameHealthMonitor(emit: emitted.Add, frozenThreshold: 3,
            streamLostTimeout: TimeSpan.FromMinutes(1));
        monitor.NoteFrame(0xAAAA, streamOpen: true, T0);
        monitor.NoteFrame(0xAAAA, streamOpen: true, T0.AddSeconds(1));
        Assert.Empty(emitted);
        monitor.NoteFrame(0xAAAA, streamOpen: true, T0.AddSeconds(2));
        var frozen = Assert.Single(emitted);
        Assert.Equal(HealthReasons.Frozen, frozen.Reason);
    }

    [Fact]
    public void Health_NewSignature_ResetsStreak()
    {
        var emitted = new List<HealthEvent>();
        var monitor = new FrameHealthMonitor(emit: emitted.Add, frozenThreshold: 3,
            streamLostTimeout: TimeSpan.FromMinutes(1));
        monitor.NoteFrame(0xAAAA, streamOpen: true, T0);
        monitor.NoteFrame(0xAAAA, streamOpen: true, T0.AddSeconds(1));
        monitor.NoteFrame(0xBBBB, streamOpen: true, T0.AddSeconds(2));
        monitor.NoteFrame(0xBBBB, streamOpen: true, T0.AddSeconds(3));
        Assert.Empty(emitted);
    }

    [Fact]
    public void Health_StreamLost_OnGap_OnlyAfterTimeout()
    {
        var emitted = new List<HealthEvent>();
        var monitor = new FrameHealthMonitor(emit: emitted.Add, frozenThreshold: 100,
            streamLostTimeout: TimeSpan.FromSeconds(5));
        monitor.NoteFrame(0x0001, streamOpen: true, T0);
        monitor.QueryIdle(T0.AddSeconds(5));
        Assert.Empty(emitted);
        monitor.QueryIdle(T0.AddSeconds(5).AddTicks(1));
        var lost = Assert.Single(emitted);
        Assert.Equal(HealthReasons.StreamLost, lost.Reason);
        // Latched: no duplicate until a new frame arrives.
        monitor.QueryIdle(T0.AddSeconds(30));
        Assert.Single(emitted);
        monitor.NoteFrame(0x0002, streamOpen: true, T0.AddSeconds(31));
        monitor.QueryIdle(T0.AddSeconds(40));
        Assert.Equal(2, emitted.Count);
    }

    [Fact]
    public void Health_ProcessExit_NonZeroEmits_ZeroSilent()
    {
        var emitted = new List<HealthEvent>();
        var monitor = new FrameHealthMonitor(emit: emitted.Add);
        monitor.NoteProcessExit(0, T0);
        Assert.Empty(emitted);
        monitor.NoteProcessExit(1, T0.AddSeconds(1));
        var failure = Assert.Single(emitted);
        Assert.Equal(HealthReasons.AnalysisFailure, failure.Reason);
    }

    [Fact]
    public async Task Upload_SendsJsonContentType()
    {
        var (client, handler) = MakeUploadClient();
        Assert.Equal(UploadResult.Uploaded, await client.UploadAsync(Payload()));
        var request = Assert.Single(handler.Requests);
        Assert.Equal("application/json", request.Content!.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Heartbeat_SendsJsonContentType()
    {
        var handler = new FakeHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var hb = new HeartbeatClient(http, "cam-01");
        await hb.SendAsync(hb.Build("sw-1", "det-1", 1, 0, 0.5, 0));
        var request = Assert.Single(handler.Requests);
        Assert.Equal("application/json", request.Content!.Headers.ContentType?.MediaType);
    }

    [Fact]
    public void Backoff_NegativeAttempt_Throws()
    {
        var (client, _) = MakeUploadClient();
        Assert.Throws<ArgumentOutOfRangeException>(() => client.BackoffDelay(-1));
    }

    [Theory]
    [InlineData(-1, 2.5, 100)]
    [InlineData(3, 2.5, -1)]
    public void Heartbeat_Build_NegativeCounters_Throw(int queueDepth, double age, long disk)
    {
        var hb = new HeartbeatClient(
            new HttpClient(new FakeHandler()) { BaseAddress = new Uri("http://localhost/") }, "cam-01");
        Assert.Throws<ArgumentOutOfRangeException>(
            () => hb.Build("sw-1", "det-1", 1, queueDepth, age, disk));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Heartbeat_Build_NonFiniteAge_Throws(double age)
    {
        var hb = new HeartbeatClient(
            new HttpClient(new FakeHandler()) { BaseAddress = new Uri("http://localhost/") }, "cam-01");
        Assert.ThrowsAny<ArgumentException>(
            () => hb.Build("sw-1", "det-1", 1, 0, age, 0));
    }

    [Fact]
    public async Task Config_Pull_ServerError_ThrowsHttpRequest()
    {
        var h = new FakeHandler();
        h.QueueStatus(HttpStatusCode.InternalServerError);
        var http = new HttpClient(h) { BaseAddress = new Uri("http://localhost/") };
        var config = new ConfigClient(http, "cam-01");
        await Assert.ThrowsAsync<HttpRequestException>(() => config.PullAsync());
    }

    [Fact]
    public void Health_FrozenEmitsOncePerStreak_FourthSilentNewSignatureRearms()
    {
        var emitted = new List<HealthEvent>();
        var monitor = new FrameHealthMonitor(emit: emitted.Add, frozenThreshold: 3,
            streamLostTimeout: TimeSpan.FromMinutes(1));
        for (var i = 0; i < 4; i++)
        {
            monitor.NoteFrame(0xAAAA, streamOpen: true, T0.AddSeconds(i));
        }

        Assert.Single(emitted); // 4th identical frame in the streak stays silent.

        // A new signature re-arms: three of the new signature emit exactly once more.
        monitor.NoteFrame(0xBBBB, streamOpen: true, T0.AddSeconds(4));
        monitor.NoteFrame(0xBBBB, streamOpen: true, T0.AddSeconds(5));
        Assert.Single(emitted);
        monitor.NoteFrame(0xBBBB, streamOpen: true, T0.AddSeconds(6));
        Assert.Equal(2, emitted.Count);
        Assert.All(emitted, e => Assert.Equal(HealthReasons.Frozen, e.Reason));
    }

    [Fact]
    public void Health_PlaceholderDoesNotRefreshIdleClock()
    {
        var emitted = new List<HealthEvent>();
        var monitor = new FrameHealthMonitor(emit: emitted.Add, frozenThreshold: 100,
            streamLostTimeout: TimeSpan.FromSeconds(10));
        monitor.NoteFrame(0x0001, streamOpen: true, T0);

        // Placeholder while down must NOT move the idle clock: the gap is measured
        // from the last real (stream-open) frame, so T0+11s is already lost.
        monitor.NoteFrame(0xFFFF, streamOpen: false, T0.AddSeconds(100));
        monitor.QueryIdle(T0.AddSeconds(11));
        var lost = Assert.Single(emitted);
        Assert.Equal(HealthReasons.StreamLost, lost.Reason);
    }

    [Fact]
    public void Health_ProcessExit_RepeatedCallsReEmit_CallerMustEdgeTrigger()
    {
        var emitted = new List<HealthEvent>();
        var monitor = new FrameHealthMonitor(emit: emitted.Add);
        monitor.NoteProcessExit(1, T0);
        monitor.NoteProcessExit(1, T0.AddSeconds(1));
        Assert.Equal(2, emitted.Count); // No latch: the caller must edge-trigger.
    }

    [Fact]
    public void Health_EventShape_IsEFamily()
    {
        var emitted = new List<HealthEvent>();
        var monitor = new FrameHealthMonitor(emit: emitted.Add);
        monitor.NoteProcessExit(2, T0);
        var ev = Assert.Single(emitted);
        Assert.Equal("E", ev.Type);
        Assert.Equal("health", ev.RuleId);
        Assert.Equal(1, ev.RuleVersion);
        Assert.Equal(T0, ev.EventTimeUtc);
    }
}
