using EduSphere.Safety.Edge;
using Xunit;

public sealed class EdgeFfmpegSourceTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);

    private static FfmpegFrameSource FileSource(string url = "/tmp/sim.mp4") =>
        new("cam-1", url, 640, 480, 5.0, T0);

    // ---- URL allowlist: accept ----

    [Theory]
    [InlineData("/tmp/sim.mp4")]
    [InlineData("file:///tmp/sim.mp4")]
    [InlineData("file://localhost/tmp/sim.mp4")]
    [InlineData("rtsp://127.0.0.1:8554/cam")]
    [InlineData("rtsp://10.0.0.5/cam")]
    [InlineData("rtsp://192.168.1.20:554/x")]
    [InlineData("rtsp://172.16.0.9/y")]
    [InlineData("rtsp://172.31.255.1/z")]
    [InlineData("rtsp://localhost/cam")]
    [InlineData("rtsp://user:pass@192.168.1.2/cam")]
    [InlineData("rtsp://user@192.168.1.2/cam")] // bare userinfo (no password): host still allowlisted
    [InlineData("rtsp://[::1]/cam")]
    [InlineData("rtsp://2130706433/cam")] // decimal 127.0.0.1: classed by parsed address (loopback)
    [InlineData("rtsp://0177.0.0.1/cam")] // octal 127.0.0.1: same rule
    public void ValidateSourceUrl_Allowlisted_Accepts(string url)
    {
        var mode = FfmpegFrameSource.ValidateSourceUrl(url);
        Assert.Equal(url.StartsWith("rtsp:", StringComparison.Ordinal) ? FrameSourceMode.Rtsp : FrameSourceMode.File, mode);
    }

    // ---- URL allowlist: reject (before any spawn) ----

    [Theory]
    [InlineData("concat:seg1.mp4|seg2.mp4")]
    [InlineData("pipe:0")]
    [InlineData("http://10.0.0.5/cam.mjpg")]
    [InlineData("https://10.0.0.5/cam.mjpg")]
    [InlineData("rtmp://10.0.0.5/live")]
    [InlineData("udp://10.0.0.5:5000")]
    [InlineData("rtsp://8.8.8.8/cam")]
    [InlineData("rtsp://203.0.113.5/cam")]
    [InlineData("rtsp://169.254.169.254/latest/meta-data")] // cloud metadata IP
    [InlineData("rtsp://169.254.10.20/cam")] // link-local range
    [InlineData("rtsp://example.com/cam")] // DNS hostname: gated, rejected here
    [InlineData("rtsp://[2001:db8::1]/cam")] // IPv6 non-loopback: rejected
    [InlineData("rtsp://[fe80::1]/cam")] // IPv6 link-local: rejected
    [InlineData("rtsp://16843009/cam")] // decimal 1.1.1.1 (public): classed by parsed address
    [InlineData("rtsp://010.0.0.5/cam")] // octal 8.0.0.5 (public): same rule
    [InlineData("rtsp://%31%32%37.0.0.1/cam")] // percent-encoded host: not a valid URI here
    [InlineData("rtsp:///cam")]
    [InlineData("relative/path.mp4")]
    [InlineData("sim.mp4")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("file://remotehost/tmp/x.mp4")]
    public void ValidateSourceUrl_NotAllowlisted_Rejects(string url)
    {
        Assert.Throws<ArgumentException>(() => FfmpegFrameSource.ValidateSourceUrl(url));
    }

    [Fact]
    public void Ctor_RejectedUrl_ThrowsBeforeSpawn()
    {
        Assert.Throws<ArgumentException>(() => FileSource("pipe:0"));
    }

    // ---- argv construction (tokens, never a shell string) ----

    [Fact]
    public void BuildArgumentList_FileMode_ExactTokens()
    {
        var argv = FfmpegFrameSource.BuildArgumentList(FrameSourceMode.File, "/tmp/sim.mp4", 640, 480, 5.0);
        Assert.Equal(
            ["-hide_banner", "-loglevel", "error", "-re",
             "-protocol_whitelist", "file", "-i", "/tmp/sim.mp4",
             "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", "640x480", "-r", "5", "-"],
            argv);
    }

    [Fact]
    public void BuildArgumentList_Whitelist_ImmediatelyBeforeInput()
    {
        var file = FfmpegFrameSource.BuildArgumentList(
            FrameSourceMode.File, "/tmp/sim.mp4", 640, 480, 5.0).ToList();
        var fileTag = file.IndexOf("-protocol_whitelist");
        Assert.True(fileTag >= 0 && file[fileTag + 1] == "file");
        Assert.Equal(fileTag + 2, file.IndexOf("-i"));

        var rtsp = FfmpegFrameSource.BuildArgumentList(
            FrameSourceMode.Rtsp, "rtsp://127.0.0.1:8554/cam", 640, 480, 5.0, "tcp").ToList();
        var rtspTag = rtsp.IndexOf("-protocol_whitelist");
        Assert.True(rtspTag >= 0 && rtsp[rtspTag + 1] == "rtsp,rtp,tcp,udp");
        Assert.Equal(rtspTag + 2, rtsp.IndexOf("-i"));
    }

    [Fact]
    public void BuildArgumentList_FileUrl_LocalPathPassed()
    {
        var argv = FfmpegFrameSource.BuildArgumentList(FrameSourceMode.File, "file:///tmp/sim.mp4", 640, 480, 5.0).ToList();
        Assert.Equal("/tmp/sim.mp4", argv[argv.IndexOf("-i") + 1]);
    }

    [Fact]
    public void BuildArgumentList_RtspMode_TransportBeforeInput()
    {
        var argv = FfmpegFrameSource.BuildArgumentList(
            FrameSourceMode.Rtsp, "rtsp://127.0.0.1:8554/cam", 640, 480, 5.0, "tcp").ToList();
        Assert.DoesNotContain("-re", argv);
        var transportAt = argv.IndexOf("-rtsp_transport");
        var inputAt = argv.IndexOf("-i");
        Assert.True(transportAt >= 0 && inputAt > transportAt);
        Assert.Equal("tcp", argv[transportAt + 1]);
        Assert.Equal("rtsp://127.0.0.1:8554/cam", argv[inputAt + 1]);
        Assert.Equal("-", argv[^1]); // stdout pipe, never a file target
    }

    // ---- constructor guards ----

    [Theory]
    [InlineData(0, 480)]
    [InlineData(640, -1)]
    public void Ctor_BadDimensions_Throws(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FfmpegFrameSource("cam-1", "/tmp/sim.mp4", width, height, 5.0, T0));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Ctor_BadFrameRate_Throws(double fps)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FfmpegFrameSource("cam-1", "/tmp/sim.mp4", 640, 480, fps, T0));
    }

    [Fact]
    public void Ctor_BadTransport_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new FfmpegFrameSource("cam-1", "rtsp://127.0.0.1/cam", 640, 480, 5.0, T0, transport: "quic"));
    }

    [Fact]
    public void Ctor_EmptyCameraId_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new FfmpegFrameSource(" ", "/tmp/sim.mp4", 640, 480, 5.0, T0));
    }

    // ---- rawvideo parser ----

    [Fact]
    public void TryReadFrame_TwoFramesThenCleanEof()
    {
        var frame = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        using var stream = new MemoryStream([.. frame, .. frame]);
        var buffer = new byte[12];
        Assert.True(FfmpegFrameSource.TryReadFrame(stream, buffer));
        Assert.Equal(frame, buffer);
        Assert.True(FfmpegFrameSource.TryReadFrame(stream, buffer));
        Assert.False(FfmpegFrameSource.TryReadFrame(stream, buffer)); // clean EOF on boundary
    }

    [Fact]
    public void TryReadFrame_TornMidFrame_Throws()
    {
        using var stream = new MemoryStream(new byte[20]); // 12 + 8: second frame torn
        Assert.True(FfmpegFrameSource.TryReadFrame(stream, new byte[12]));
        Assert.Throws<IOException>(() => FfmpegFrameSource.TryReadFrame(stream, new byte[12]));
    }

    // ---- credential redaction ----

    [Fact]
    public void RedactCredentials_Password_Masked()
    {
        var redacted = FfmpegFrameSource.RedactCredentials("rtsp://admin:s3cret@10.0.0.1:554/cam");
        Assert.Equal("rtsp://admin:***@10.0.0.1:554/cam", redacted);
        Assert.DoesNotContain("s3cret", redacted);
    }

    [Theory]
    [InlineData("rtsp://10.0.0.1/cam", "rtsp://10.0.0.1/cam")] // no userinfo: untouched
    [InlineData("frame 12 decoded", "frame 12 decoded")] // non-URL line: untouched
    [InlineData(null, "")]
    [InlineData("", "")]
    public void RedactCredentials_NonSecret_Passthrough(string? input, string expected)
    {
        Assert.Equal(expected, FfmpegFrameSource.RedactCredentials(input));
    }

    [Fact]
    public void RedactCredentials_StderrLineWithUrl_MasksPassword()
    {
        var line = "[rtsp @ 0x123] auth as user rtsp://ops:p4ssw0rd@192.168.1.2:554/cam failed";
        var redacted = FfmpegFrameSource.RedactCredentials(line);
        Assert.DoesNotContain("p4ssw0rd", redacted);
        Assert.Contains("ops:***@", redacted);
    }

    [Fact]
    public void RedactCredentials_AllSpans_Masked()
    {
        var line = "from rtsp://u1:p1@10.0.0.1/x to rtsp://u2:p2@10.0.0.2:554/y done";
        var redacted = FfmpegFrameSource.RedactCredentials(line);
        Assert.Equal("from rtsp://u1:***@10.0.0.1/x to rtsp://u2:***@10.0.0.2:554/y done", redacted);
    }

    [Fact]
    public void RedactCredentials_BareUserinfo_Passthrough()
    {
        // User without password: nothing secret to mask — even beside a real secret.
        var line = "rtsp://user@10.0.0.1/x then rtsp://ops:pw@10.0.0.2/y";
        var redacted = FfmpegFrameSource.RedactCredentials(line);
        Assert.Contains("rtsp://user@10.0.0.1/x", redacted);
        Assert.Contains("ops:***@", redacted);
        Assert.DoesNotContain(":pw@", redacted);
    }

    [Fact]
    public void Dispose_Twice_DoesNotThrow()
    {
        var source = FileSource();
        source.Dispose();
        var ex = Record.Exception(source.Dispose);
        Assert.Null(ex);
    }

    // ---- frame hash (frozen-detection signature) ----

    [Fact]
    public void FrameHash_Empty_IsFnvOffsetBasis()
    {
        Assert.Equal(14695981039346656037ul, FfmpegFrameSource.FrameHash([]));
    }

    [Fact]
    public void FrameHash_DeterministicAndAvalanching()
    {
        var a = new byte[] { 10, 20, 30, 40 };
        var b = new byte[] { 10, 20, 30, 41 };
        Assert.Equal(FfmpegFrameSource.FrameHash(a), FfmpegFrameSource.FrameHash(a));
        Assert.NotEqual(FfmpegFrameSource.FrameHash(a), FfmpegFrameSource.FrameHash(b));
    }
}
