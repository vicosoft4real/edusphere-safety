using EduSphere.Safety.Edge;
using Xunit;

public sealed class EdgePreprocessTests
{
    // ---- Letterbox ----

    [Fact]
    public void Letterbox_IdentityScale_NoPads()
    {
        var p = Letterbox.Compute(640, 640, 640, 640);
        Assert.Equal(1.0, p.Scale);
        Assert.Equal(640, p.ScaledWidth);
        Assert.Equal(640, p.ScaledHeight);
        Assert.Equal(0, p.PadLeft);
        Assert.Equal(0, p.PadTop);
    }

    [Fact]
    public void Letterbox_WideIntoSquare_PadsTopBottom()
    {
        var p = Letterbox.Compute(640, 480, 640, 640);
        Assert.Equal(1.0, p.Scale);
        Assert.Equal(640, p.ScaledWidth);
        Assert.Equal(480, p.ScaledHeight);
        Assert.Equal(0, p.PadLeft);
        Assert.Equal(80, p.PadTop);
    }

    [Fact]
    public void Letterbox_TallIntoSquare_PadsLeftRight()
    {
        var p = Letterbox.Compute(480, 640, 640, 640);
        Assert.Equal(1.0, p.Scale);
        Assert.Equal(480, p.ScaledWidth);
        Assert.Equal(640, p.ScaledHeight);
        Assert.Equal(80, p.PadLeft);
        Assert.Equal(0, p.PadTop);
    }

    [Fact]
    public void Letterbox_SquareIntoWide_PadsLeftRight()
    {
        // 500x500 into 640x480: scale = min(1.28, 0.96) = 0.96 -> 480x480, pads 80/0.
        var p = Letterbox.Compute(500, 500, 640, 480);
        Assert.Equal(0.96, p.Scale, precision: 12);
        Assert.Equal(480, p.ScaledWidth);
        Assert.Equal(480, p.ScaledHeight);
        Assert.Equal(80, p.PadLeft);
        Assert.Equal(0, p.PadTop);
    }

    [Fact]
    public void Letterbox_OddDimensions_RoundsInsteadOfTruncating()
    {
        // 641 px at scale 640/641 = 639.9999... must round to 640, not truncate to 639.
        var p = Letterbox.Compute(641, 479, 640, 640);
        Assert.Equal(640, p.ScaledWidth);
        Assert.Equal(478, p.ScaledHeight); // 479 * 640/641 = 478.25... -> 478
        Assert.Equal(0, p.PadLeft);
        Assert.Equal(81, p.PadTop); // (640 - 478) / 2, leftover pixel to the bottom
    }

    [Fact]
    public void Letterbox_DownscaleWide_PadsTop()
    {
        // 1280x720 into 640x640: scale 0.5 -> 640x360, padTop 140.
        var p = Letterbox.Compute(1280, 720, 640, 640);
        Assert.Equal(0.5, p.Scale, precision: 12);
        Assert.Equal(640, p.ScaledWidth);
        Assert.Equal(360, p.ScaledHeight);
        Assert.Equal(0, p.PadLeft);
        Assert.Equal(140, p.PadTop);
    }

    [Fact]
    public void Letterbox_MapToSourcePixels_RoundTripsCanvasPastePoint()
    {
        var p = Letterbox.Compute(640, 480, 640, 640); // scale 1, padTop 80
        var (x, y) = p.MapToSourcePixels(320, 80 + 240);
        Assert.Equal(320.0, x);
        Assert.Equal(240.0, y);
    }

    [Fact]
    public void Letterbox_MapBoxToSourceNormalized_FullCanvasCoversFullFrame()
    {
        var p = Letterbox.Compute(640, 480, 640, 640);
        var (x, y, w, h) = p.MapBoxToSourceNormalized(0, 80, 640, 480, 640, 480);
        Assert.Equal(0.0, x);
        Assert.Equal(0.0, y);
        Assert.Equal(1.0, w, precision: 12);
        Assert.Equal(1.0, h, precision: 12);
    }

    [Theory]
    [InlineData(0, 480, 640, 640)]
    [InlineData(640, 0, 640, 640)]
    [InlineData(640, 480, 0, 640)]
    [InlineData(640, 480, 640, -1)]
    public void Letterbox_NonPositiveDimension_Throws(int sw, int sh, int dw, int dh)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Letterbox.Compute(sw, sh, dw, dh));
    }

    // ---- NMS: IoU ----

    [Fact]
    public void IoU_IdenticalBoxes_One()
    {
        var b = new BoundingBox(0.1, 0.2, 0.3, 0.4);
        Assert.Equal(1.0, Nms.IoU(b, b), precision: 12);
    }

    [Fact]
    public void IoU_DisjointBoxes_Zero()
    {
        Assert.Equal(0.0, Nms.IoU(
            new BoundingBox(0.0, 0.0, 0.2, 0.2),
            new BoundingBox(0.5, 0.5, 0.2, 0.2)));
    }

    [Fact]
    public void IoU_PartialOverlap_ExactValue()
    {
        // Inter 0.25*0.25 = 0.0625; union 0.25 + 0.25 - 0.0625 = 0.4375; IoU = 1/7.
        var iou = Nms.IoU(
            new BoundingBox(0.0, 0.0, 0.5, 0.5),
            new BoundingBox(0.25, 0.25, 0.5, 0.5));
        Assert.Equal(1.0 / 7.0, iou, precision: 12);
    }

    // ---- NMS: Suppress ----

    [Fact]
    public void Suppress_OverlappingPair_KeepsArgmax()
    {
        var boxes = new BoundingBox[]
        {
            new(0.1, 0.1, 0.4, 0.4),
            new(0.12, 0.12, 0.4, 0.4), // heavy overlap with box 0
        };
        var kept = Nms.Suppress(boxes, new[] { 0.6, 0.9 }, iouThreshold: 0.5, scoreThreshold: 0.0, maxDetections: 10);
        Assert.Equal(new[] { 1 }, kept);
    }

    [Fact]
    public void Suppress_DisjointBoxes_KeepsBothScoreOrder()
    {
        var boxes = new BoundingBox[]
        {
            new(0.0, 0.0, 0.2, 0.2),
            new(0.7, 0.7, 0.2, 0.2),
        };
        var kept = Nms.Suppress(boxes, new[] { 0.4, 0.8 }, iouThreshold: 0.5, scoreThreshold: 0.0, maxDetections: 10);
        Assert.Equal(new[] { 1, 0 }, kept);
    }

    [Fact]
    public void Suppress_EmptyIn_EmptyOut()
    {
        var kept = Nms.Suppress(
            Array.Empty<BoundingBox>(), Array.Empty<double>(),
            iouThreshold: 0.5, scoreThreshold: 0.0, maxDetections: 10);
        Assert.Empty(kept);
    }

    [Fact]
    public void Suppress_MaxCap_TruncatesToTopScores()
    {
        var boxes = new BoundingBox[]
        {
            new(0.0, 0.0, 0.1, 0.1),
            new(0.2, 0.0, 0.1, 0.1),
            new(0.4, 0.0, 0.1, 0.1),
            new(0.6, 0.0, 0.1, 0.1),
            new(0.8, 0.0, 0.1, 0.1),
        };
        var kept = Nms.Suppress(boxes, new[] { 0.1, 0.5, 0.9, 0.3, 0.7 },
            iouThreshold: 0.5, scoreThreshold: 0.0, maxDetections: 2);
        Assert.Equal(new[] { 2, 4 }, kept);
    }

    [Fact]
    public void Suppress_ScoreExactlyAtThreshold_IsKept()
    {
        var boxes = new BoundingBox[] { new(0.0, 0.0, 0.2, 0.2), new(0.7, 0.7, 0.2, 0.2) };
        var kept = Nms.Suppress(boxes, new[] { 0.5, 0.49 }, iouThreshold: 0.5, scoreThreshold: 0.5, maxDetections: 10);
        Assert.Equal(new[] { 0 }, kept);
    }

    [Fact]
    public void Suppress_EqualScores_DeterministicTieByIndex()
    {
        var boxes = new BoundingBox[]
        {
            new(0.0, 0.0, 0.2, 0.2),
            new(0.7, 0.7, 0.2, 0.2),
            new(0.0, 0.7, 0.2, 0.2),
        };
        var first = Nms.Suppress(boxes, new[] { 0.8, 0.8, 0.8 }, iouThreshold: 0.5, scoreThreshold: 0.0, maxDetections: 3);
        var second = Nms.Suppress(boxes, new[] { 0.8, 0.8, 0.8 }, iouThreshold: 0.5, scoreThreshold: 0.0, maxDetections: 3);
        Assert.Equal(new[] { 0, 1, 2 }, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Suppress_IoUExactlyAtThreshold_IsKept()
    {
        var a = new BoundingBox(0.0, 0.0, 0.5, 0.5);
        var b = new BoundingBox(0.25, 0.25, 0.5, 0.5); // IoU exactly 1/7
        var iou = Nms.IoU(a, b);
        var kept = Nms.Suppress(new[] { a, b }, new[] { 0.9, 0.8 }, iouThreshold: iou, scoreThreshold: 0.0, maxDetections: 10);
        Assert.Equal(new[] { 0, 1 }, kept);
    }

    [Fact]
    public void Suppress_MismatchedLengths_Throws()
    {
        var boxes = new BoundingBox[] { new(0.0, 0.0, 0.2, 0.2) };
        Assert.Throws<ArgumentException>(() =>
            Nms.Suppress(boxes, new[] { 0.9, 0.8 }, iouThreshold: 0.5, scoreThreshold: 0.0, maxDetections: 10));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Suppress_BadThresholds_Throw(double bad)
    {
        var boxes = new BoundingBox[] { new(0.0, 0.0, 0.2, 0.2) };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Nms.Suppress(boxes, new[] { 0.9 }, iouThreshold: bad, scoreThreshold: 0.0, maxDetections: 10));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Nms.Suppress(boxes, new[] { 0.9 }, iouThreshold: 0.5, scoreThreshold: bad, maxDetections: 10));
    }

    [Fact]
    public void Suppress_ZeroMaxDetections_Throws()
    {
        var boxes = new BoundingBox[] { new(0.0, 0.0, 0.2, 0.2) };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Nms.Suppress(boxes, new[] { 0.9 }, iouThreshold: 0.5, scoreThreshold: 0.0, maxDetections: 0));
    }

    // ---- ImageSampling ----

    [Fact]
    public void Resample_ConstantImage_ConstantPlanes()
    {
        var rgb = new byte[4 * 3 * 3];
        for (var i = 0; i < rgb.Length; i += 3)
        {
            rgb[i] = 10;
            rgb[i + 1] = 20;
            rgb[i + 2] = 30;
        }

        var p = Letterbox.Compute(4, 3, 4, 3);
        var nchw = ImageSampling.LetterboxRgb24ToNchwFloat(rgb, 4, 3, p, padGray: 114, divisor: 1.0);

        Assert.Equal(3 * 4 * 3, nchw.Length);
        for (var y = 0; y < 3; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                Assert.Equal(10f, nchw[y * 4 + x]);
                Assert.Equal(20f, nchw[12 + (y * 4 + x)]);
                Assert.Equal(30f, nchw[24 + (y * 4 + x)]);
            }
        }
    }

    [Fact]
    public void Resample_OneToOneIdentity_OutputEqualsInputOverDivisor()
    {
        var rgb = new byte[5 * 4 * 3];
        for (var i = 0; i < rgb.Length; i++)
        {
            rgb[i] = (byte)((i * 37 + 11) % 251);
        }

        var p = Letterbox.Compute(5, 4, 5, 4); // scale 1, no pads
        const double divisor = 2.0;
        var nchw = ImageSampling.LetterboxRgb24ToNchwFloat(rgb, 5, 4, p, padGray: 114, divisor: divisor);

        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 5; x++)
            {
                var o = (y * 5 + x) * 3;
                Assert.Equal((float)(rgb[o] / divisor), nchw[y * 5 + x]);
                Assert.Equal((float)(rgb[o + 1] / divisor), nchw[20 + (y * 5 + x)]);
                Assert.Equal((float)(rgb[o + 2] / divisor), nchw[40 + (y * 5 + x)]);
            }
        }
    }

    [Fact]
    public void Resample_PadRegion_EmitsPadGrayOverDivisor()
    {
        // 640x480 -> 640x640: scale 1, padTop 80. Black image: every pad cell is gray,
        // the pasted image stays black.
        var rgb = new byte[640 * 480 * 3];
        var p = Letterbox.Compute(640, 480, 640, 640);
        const double divisor = 255.0;
        var nchw = ImageSampling.LetterboxRgb24ToNchwFloat(rgb, 640, 480, p, padGray: 114, divisor: divisor);

        var expectedPad = (float)(114 / divisor);
        var plane = 640 * 640;
        var corners = new[] { (0, 0), (639, 0), (0, 639), (639, 639) };
        foreach (var (x, y) in corners)
        {
            Assert.Equal(expectedPad, nchw[y * 640 + x]);
            Assert.Equal(expectedPad, nchw[plane + (y * 640 + x)]);
            Assert.Equal(expectedPad, nchw[(2 * plane) + (y * 640 + x)]);
        }

        // Image center (canvas (320, 80 + 240)) samples the black source.
        Assert.Equal(0f, nchw[320 * 640 + 320]);
        Assert.Equal(0f, nchw[plane + (320 * 640 + 320)]);
        Assert.Equal(0f, nchw[(2 * plane) + (320 * 640 + 320)]);
    }

    [Fact]
    public void Resample_SingleBrightPixel_LandsInExpectedNchwCell()
    {
        // Bilinear exactness on integer grid points: a 1:1 canvas pixel reproduces its
        // texel, and the untouched neighbour stays zero.
        var rgb = new byte[4 * 4 * 3];
        var o = (1 * 4 + 2) * 3;
        rgb[o] = 255;
        rgb[o + 1] = 128;
        rgb[o + 2] = 64;

        var p = Letterbox.Compute(4, 4, 4, 4);
        var nchw = ImageSampling.LetterboxRgb24ToNchwFloat(rgb, 4, 4, p, padGray: 0, divisor: 1.0);

        var plane = 4 * 4;
        Assert.Equal(255f, nchw[(1 * 4 + 2)]);
        Assert.Equal(128f, nchw[plane + (1 * 4 + 2)]);
        Assert.Equal(64f, nchw[(2 * plane) + (1 * 4 + 2)]);
        Assert.Equal(0f, nchw[(1 * 4 + 1)]);
        Assert.Equal(0f, nchw[plane + (1 * 4 + 1)]);
        Assert.Equal(0f, nchw[(2 * plane) + (1 * 4 + 1)]);
    }

    [Fact]
    public void Resample_IntegerGridPoints_ReproduceExactTexels()
    {
        // 2x2 -> 4x4 (scale 2, no pads): even canvas coords hit exact texels;
        // the canvas center averages the four texels.
        var rgb = new byte[]
        {
            10, 20, 30, 40, 50, 60,
            70, 80, 90, 100, 110, 120,
        };
        var p = Letterbox.Compute(2, 2, 4, 4);
        var nchw = ImageSampling.LetterboxRgb24ToNchwFloat(rgb, 2, 2, p, padGray: 0, divisor: 1.0);

        var plane = 4 * 4;
        Assert.Equal(40f, nchw[0 * 4 + 2]); // canvas (2,0) -> source (1,0): R
        Assert.Equal(50f, nchw[plane + (0 * 4 + 2)]);
        Assert.Equal(60f, nchw[(2 * plane) + (0 * 4 + 2)]);
        Assert.Equal(70f, nchw[2 * 4 + 0]); // canvas (0,2) -> source (0,1): R
        // canvas (3,3) -> source (1.5,1.5), clamped to the (1,1) corner texel: R
        Assert.Equal(100f, nchw[3 * 4 + 3]);
        Assert.Equal(110f, nchw[plane + (3 * 4 + 3)]);
        Assert.Equal(120f, nchw[(2 * plane) + (3 * 4 + 3)]);
    }

    [Fact]
    public void Resample_Divisor_ScalesValues()
    {
        var rgb = new byte[] { 200, 100, 50 };
        var p = Letterbox.Compute(1, 1, 1, 1);

        var raw = ImageSampling.LetterboxRgb24ToNchwFloat(rgb, 1, 1, p, padGray: 0, divisor: 1.0);
        Assert.Equal(new[] { 200f, 100f, 50f }, raw);

        var norm = ImageSampling.LetterboxRgb24ToNchwFloat(rgb, 1, 1, p, padGray: 0, divisor: 255.0);
        Assert.Equal((float)(200 / 255.0), norm[0]);
        Assert.Equal((float)(100 / 255.0), norm[1]);
        Assert.Equal((float)(50 / 255.0), norm[2]);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Resample_BadDivisor_Throws(double divisor)
    {
        var rgb = new byte[1 * 1 * 3];
        var p = Letterbox.Compute(1, 1, 1, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ImageSampling.LetterboxRgb24ToNchwFloat(rgb, 1, 1, p, padGray: 0, divisor: divisor));
    }

    [Fact]
    public void Resample_SpanLengthMismatch_Throws()
    {
        var rgb = new byte[2 * 2 * 3 - 1];
        var p = Letterbox.Compute(2, 2, 2, 2);
        Assert.Throws<ArgumentException>(() =>
            ImageSampling.LetterboxRgb24ToNchwFloat(rgb, 2, 2, p, padGray: 0, divisor: 1.0));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(2, -1)]
    public void Resample_NonPositiveSource_Throws(int srcW, int srcH)
    {
        var p = Letterbox.Compute(2, 2, 2, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ImageSampling.LetterboxRgb24ToNchwFloat(Array.Empty<byte>(), srcW, srcH, p, padGray: 0, divisor: 1.0));
    }

    [Fact]
    public void Resample_NonPositiveScale_Throws()
    {
        var rgb = new byte[2 * 2 * 3];
        var bad = new LetterboxParams(0.0, 2, 2, 0, 0, 2, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ImageSampling.LetterboxRgb24ToNchwFloat(rgb, 2, 2, bad, padGray: 0, divisor: 1.0));
    }
}
