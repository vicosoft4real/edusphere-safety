using EduSphere.Safety.Edge;
using Xunit;

public sealed class EdgeFrameTests
{
    private static Frame MakeFrame(long number = 0, int width = 640, int height = 480)
        => new("cam-1", number, new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero), width, height);

    [Fact]
    public void FootPoint_CenteredBox_IsBottomCenter()
    {
        var detection = new Detection(new BoundingBox(0.25, 0.25, 0.5, 0.5), 0.9, Detection.PersonClassId);

        var foot = detection.FootPoint;

        Assert.Equal(0.5, foot.X, precision: 9);
        Assert.Equal(0.75, foot.Y, precision: 9);
    }

    [Fact]
    public void FootPoint_FullFrameBox_IsBottomCenterOfFrame()
    {
        var detection = new Detection(new BoundingBox(0, 0, 1, 1), 0.9, Detection.PersonClassId);

        var foot = detection.FootPoint;

        Assert.Equal(0.5, foot.X, precision: 9);
        Assert.Equal(1.0, foot.Y, precision: 9);
    }

    [Fact]
    public void FootPoint_BottomRightCornerBox_TouchesCorner()
    {
        var detection = new Detection(new BoundingBox(0.8, 0.8, 0.2, 0.2), 0.9, Detection.PersonClassId);

        var foot = detection.FootPoint;

        Assert.Equal(0.9, foot.X, precision: 9);
        Assert.Equal(1.0, foot.Y, precision: 9);
    }

    [Fact]
    public void FootPoint_LeftEdgeBox_StaysOnEdge()
    {
        var detection = new Detection(new BoundingBox(0, 0.5, 0.2, 0.3), 0.9, Detection.PersonClassId);

        var foot = detection.FootPoint;

        Assert.Equal(0.1, foot.X, precision: 9);
        Assert.Equal(0.8, foot.Y, precision: 9);
    }

    [Fact]
    public void BoundingBox_NegativeComponent_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundingBox(-0.1, 0.2, 0.3, 0.3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundingBox(0.1, -0.1, 0.3, 0.3));
    }

    [Fact]
    public void BoundingBox_ComponentGreaterThanOne_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundingBox(0.1, 0.2, 1.5, 0.3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundingBox(0.1, 0.2, 0.3, 2.0));
    }

    [Fact]
    public void BoundingBox_ExtendingPastFrameEdge_Throws()
    {
        // X + W and Y + H must stay inside the normalized frame.
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundingBox(0.9, 0.1, 0.2, 0.3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundingBox(0.1, 0.9, 0.3, 0.2));
    }

    [Fact]
    public void Frame_NonPositiveDimensions_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Frame("cam-1", 0, DateTimeOffset.UtcNow, 0, 480));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Frame("cam-1", 0, DateTimeOffset.UtcNow, 640, -1));
    }

    [Fact]
    public void Frame_NegativeNumber_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Frame("cam-1", -1, DateTimeOffset.UtcNow, 640, 480));
    }

    // --- Pixels: Rgb24 row-major payload (Length == W*H*3 when present) ---

    [Fact]
    public void Frame_DefaultPixels_Empty()
    {
        Assert.True(MakeFrame().Pixels.IsEmpty);
    }

    [Fact]
    public void Frame_PixelsCorrectLength_Accepted()
    {
        var frame = new Frame("cam-1", 0, DateTimeOffset.UtcNow, 4, 2, Pixels: new byte[4 * 2 * 3]);

        Assert.Equal(4 * 2 * 3, frame.Pixels.Length);
    }

    [Fact]
    public void Frame_PixelsWrongLength_ThrowsInCtor()
    {
        Assert.Throws<ArgumentException>(() =>
            new Frame("cam-1", 0, DateTimeOffset.UtcNow, 640, 480, Pixels: new byte[10]));
    }

    [Fact]
    public void Frame_PixelsWrongLength_ThrowsInWithAndInitializer()
    {
        var frame = MakeFrame();

        Assert.Throws<ArgumentException>(() => frame with { Pixels = new byte[10] });
        Assert.Throws<ArgumentException>(() =>
            new Frame("cam-1", 0, DateTimeOffset.UtcNow, 640, 480) { Pixels = new byte[640 * 480] });
    }

    [Fact]
    public void Frame_PixelsRetained_ByReference()
    {
        var bytes = new byte[640 * 480 * 3];
        bytes[0] = 0xAB;
        var frame = MakeFrame() with { Pixels = bytes };

        Assert.Equal(0xAB, frame.Pixels.Span[0]);
    }

    [Fact]
    public void Frame_EmptyCameraId_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new Frame("", 0, DateTimeOffset.UtcNow, 640, 480));
    }

    [Fact]
    public void Detection_NonPersonClassId_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new Detection(new BoundingBox(0.1, 0.1, 0.2, 0.2), 0.9, 1));
    }

    [Fact]
    public void Detection_ConfidenceOutOfRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Detection(new BoundingBox(0.1, 0.1, 0.2, 0.2), -0.1, Detection.PersonClassId));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Detection(new BoundingBox(0.1, 0.1, 0.2, 0.2), 1.1, Detection.PersonClassId));
    }

    [Fact]
    public async Task Source_MonotonicNumbers_AreStrictlyIncreasing()
    {
        // Monotonic frame numbers are enforced by the IFrameSource contract
        // (implementations must yield strictly increasing numbers per camera);
        // Frame construction alone cannot check cross-frame ordering.
        IFrameSource source = new ScriptedFrameSource("cam-1", startNumber: 7, count: 4);

        var numbers = new List<long>();
        await foreach (var frame in source.GetFramesAsync(CancellationToken.None))
        {
            numbers.Add(frame.Number);
        }

        Assert.Equal(numbers.OrderBy(n => n).Distinct().ToList(), numbers);
        Assert.True(numbers.Zip(numbers.Skip(1), (a, b) => b > a).All(grew => grew));
    }

    [Fact]
    public async Task FakeDetector_EmptyFrame_ReturnsNoDetections()
    {
        var detector = new FakeDetector(new Dictionary<long, IReadOnlyList<BoundingBox>>());

        var result = await detector.DetectAsync(MakeFrame(0), CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task FakeDetector_ScriptedFrames_ReturnExpectedBoxes()
    {
        var boxes = new Dictionary<long, IReadOnlyList<BoundingBox>>
        {
            [1] = [new BoundingBox(0.1, 0.1, 0.2, 0.3)],
            [2] = [new BoundingBox(0.1, 0.1, 0.2, 0.3), new BoundingBox(0.6, 0.5, 0.2, 0.4)],
        };
        var detector = new FakeDetector(boxes);

        var single = await detector.DetectAsync(MakeFrame(1), CancellationToken.None);
        var multi = await detector.DetectAsync(MakeFrame(2), CancellationToken.None);

        Assert.Single(single);
        Assert.Equal(2, multi.Count);
        Assert.Equal(0.1, multi[0].Box.X, precision: 9);
        Assert.Equal(0.7, multi[1].FootPoint.X, precision: 9);
    }

    [Fact]
    public async Task FakeDetector_AllDetectionsArePersonClass()
    {
        var boxes = new Dictionary<long, IReadOnlyList<BoundingBox>>
        {
            [0] = [new BoundingBox(0.1, 0.1, 0.2, 0.3), new BoundingBox(0.5, 0.5, 0.2, 0.2)],
        };
        var detector = new FakeDetector(boxes);

        var result = await detector.DetectAsync(MakeFrame(0), CancellationToken.None);

        Assert.All(result, d => Assert.Equal(Detection.PersonClassId, d.ClassId));
    }

    // --- Hardening: `with`/initializer bypass (init setters re-run construction validation) ---

    [Fact]
    public void Frame_WithZeroWidth_Throws()
    {
        var frame = MakeFrame();

        Assert.Throws<ArgumentOutOfRangeException>(() => frame with { Width = 0 });
    }

    [Fact]
    public void Frame_WithInvalidMembers_Throw()
    {
        var frame = MakeFrame();

        Assert.Throws<ArgumentOutOfRangeException>(() => frame with { Height = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => frame with { Number = -1 });
        Assert.Throws<ArgumentException>(() => frame with { CameraId = "" });
    }

    [Fact]
    public void BoundingBox_WithOutOfRangeX_Throws()
    {
        var box = new BoundingBox(0.1, 0.1, 0.2, 0.2);

        Assert.Throws<ArgumentOutOfRangeException>(() => box with { X = 5 });
    }

    [Fact]
    public void BoundingBox_WithMembers_ThrowExactlyAsConstruction()
    {
        var box = new BoundingBox(0.1, 0.1, 0.2, 0.2);

        Assert.Throws<ArgumentOutOfRangeException>(() => box with { Y = -0.5 });
        Assert.Throws<ArgumentOutOfRangeException>(() => box with { W = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => box with { H = 2 });
        // Moving the origin so the far edge leaves the frame must throw, like construction.
        Assert.Throws<ArgumentOutOfRangeException>(() => box with { X = 0.9 });
    }

    [Fact]
    public void Detection_InitializerConfidence_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Detection(new BoundingBox(0.1, 0.1, 0.2, 0.2), 0.9, Detection.PersonClassId) { Confidence = 99 });
    }

    [Fact]
    public void Detection_WithInvalidMembers_Throw()
    {
        var detection = new Detection(new BoundingBox(0.1, 0.1, 0.2, 0.2), 0.9, Detection.PersonClassId);

        Assert.Throws<ArgumentOutOfRangeException>(() => detection with { Confidence = -0.5 });
        Assert.Throws<ArgumentException>(() => detection with { ClassId = 1 });
        Assert.Throws<ArgumentNullException>(() => detection with { Box = null! });
    }

    // --- Hardening: zero-area boxes rejected (tracker IoU would divide by zero) ---

    [Fact]
    public void BoundingBox_ZeroArea_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundingBox(0.1, 0.1, 0, 0.2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundingBox(0.1, 0.1, 0.2, 0));
        // -0.0 equals 0, so it is zero-area too.
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundingBox(0.1, 0.1, -0.0, 0.2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundingBox(0.1, 0.1, 0.2, -0.0));

        var box = new BoundingBox(0.1, 0.1, 0.2, 0.2);
        Assert.Throws<ArgumentOutOfRangeException>(() => box with { W = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => box with { H = 0 });
    }

    // --- Hardening: TrackedPoint validated (finite doubles in 0..1 inclusive) ---

    [Fact]
    public void TrackedPoint_EdgeValues_Accepted()
    {
        Assert.Equal(0, new TrackedPoint(0, 0.5).X);
        Assert.Equal(1, new TrackedPoint(0.5, 1).Y);
    }

    [Theory]
    [InlineData(-0.1, 0.5)]
    [InlineData(0.5, 1.1)]
    [InlineData(0.5, 1 + 1e-8)]
    public void TrackedPoint_OutOfRange_Throws(double x, double y)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TrackedPoint(x, y));
    }

    [Fact]
    public void TrackedPoint_WithInvalidMember_Throws()
    {
        var point = new TrackedPoint(0.5, 0.5);

        Assert.Throws<ArgumentOutOfRangeException>(() => point with { X = 2 });
        Assert.Throws<ArgumentOutOfRangeException>(() => point with { Y = double.NaN });
    }

    // --- Hardening: non-finite inputs rejected for box components + confidence ---

    public static IEnumerable<object[]> NonFiniteValues()
    {
        yield return [double.NaN];
        yield return [double.PositiveInfinity];
        yield return [double.NegativeInfinity];
    }

    [Theory]
    [MemberData(nameof(NonFiniteValues))]
    public void BoundingBox_NonFiniteComponent_Throws(double bad)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundingBox(bad, 0.1, 0.2, 0.2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundingBox(0.1, bad, 0.2, 0.2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundingBox(0.1, 0.1, bad, 0.2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundingBox(0.1, 0.1, 0.2, bad));
    }

    [Theory]
    [MemberData(nameof(NonFiniteValues))]
    public void Detection_NonFiniteConfidence_Throws(double bad)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Detection(new BoundingBox(0.1, 0.1, 0.2, 0.2), bad, Detection.PersonClassId));
    }

    [Theory]
    [MemberData(nameof(NonFiniteValues))]
    public void TrackedPoint_NonFiniteCoordinate_Throws(double bad)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TrackedPoint(bad, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TrackedPoint(0.5, bad));
    }

    [Fact]
    public void NegativeZero_AcceptedAsZero()
    {
        var box = new BoundingBox(-0.0, -0.0, 0.2, 0.2);
        Assert.Equal(0, box.X);
        Assert.Equal(0, box.Y);

        var detection = new Detection(new BoundingBox(0.1, 0.1, 0.2, 0.2), -0.0, Detection.PersonClassId);
        Assert.Equal(0, detection.Confidence);

        Assert.Equal(0, new TrackedPoint(-0.0, -0.0).X);
    }

    // --- Hardening: 1e-9 tolerance boundaries (absorbs double rounding ~6e-7px at 640px) ---

    [Fact]
    public void BoundingBox_ComponentJustAboveOne_WithinTolerance_Passes()
    {
        // 1 + 5e-10 is inside the 1e-9 slack, applied to components and edges alike.
        var wide = new BoundingBox(0, 0, 1 + 5e-10, 0.5);
        Assert.Equal(1 + 5e-10, wide.W);

        var atEdge = new BoundingBox(1 - 5e-10, 0, 5e-10, 0.5);
        Assert.Equal(1 - 5e-10, atEdge.X);
    }

    [Theory]
    [InlineData(1e-8)]
    public void BoundingBox_ComponentBeyondTolerance_Throws(double excess)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundingBox(1 + excess, 0, 1e-10, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundingBox(0, 1 + excess, 0.5, 1e-10));
    }

    [Fact]
    public void BoundingBox_EdgeJustAboveOne_WithinTolerance_Passes()
    {
        var box = new BoundingBox(0.5, 0.5, 0.5 + 5e-10, 0.4);

        Assert.Equal(1 + 5e-10, box.X + box.W, precision: 9);
    }

    [Fact]
    public void BoundingBox_EdgeBeyondTolerance_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundingBox(0.5, 0.5, 0.5 + 1e-8, 0.4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundingBox(0.5, 0.5, 0.4, 0.5 + 1e-8));
    }

    [Fact]
    public void FootPoint_OfSlackPassingBox_MayExceedOne()
    {
        // A box whose far edge lands inside the 1e-9 slack yields a foot Y marginally
        // above 1; it stays representable, and zone checks must tolerate it.
        var detection = new Detection(new BoundingBox(0, 0.5, 0.5, 0.5 + 5e-10), 0.9, Detection.PersonClassId);

        Assert.InRange(detection.FootPoint.Y, 1.0, 1.0 + 1e-9);
    }

    private sealed class ScriptedFrameSource(string cameraId, long startNumber, int count) : IFrameSource
    {
        public string CameraId => cameraId;

        public async IAsyncEnumerable<Frame> GetFramesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return new Frame(
                    cameraId,
                    startNumber + i,
                    new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero).AddSeconds(i),
                    640,
                    480);
            }
        }
    }

    private sealed class FakeDetector(Dictionary<long, IReadOnlyList<BoundingBox>> boxesByFrame) : IPersonDetector
    {
        public Task<IReadOnlyList<Detection>> DetectAsync(Frame frame, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<Detection> result = boxesByFrame.TryGetValue(frame.Number, out var boxes)
                ? boxes.Select(b => new Detection(b, 0.9, Detection.PersonClassId)).ToList()
                : [];
            return Task.FromResult(result);
        }
    }
}
