using EduSphere.Safety.Edge;
using Xunit;

public sealed class EdgeParserTests
{
    private const int Channels = 85;

    private static float[] BlankOutput(int anchors) => new float[anchors * Channels];

    private static void SetAnchor(
        float[] output, int anchor,
        float cx, float cy, float w, float h, float obj, float personCls, float otherCls = 0f)
    {
        var row = anchor * Channels;
        output[row] = cx;
        output[row + 1] = cy;
        output[row + 2] = w;
        output[row + 3] = h;
        output[row + 4] = obj;
        output[row + 5] = personCls; // COCO class 0 = person
        output[row + 6] = otherCls; // COCO class 1
    }

    private static LetterboxParams Letterbox640x480() => Letterbox.Compute(640, 480, 640, 640);

    [Fact]
    public void Parse_CenterToCornerDecode_ExactNumbers()
    {
        // Canvas box (288, 296, 64, 48) on the 640x480 -> 640x640 letterbox (padTop 80):
        // source px (288, 216, 64, 48) -> normalized (0.45, 0.45, 0.1, 0.1).
        var output = BlankOutput(4);
        SetAnchor(output, 2, cx: 320f, cy: 320f, w: 64f, h: 48f, obj: 1f, personCls: 1f);

        var parser = new YoloxOutputParser();
        var got = parser.Parse(output, new[] { 1, 4, 85 }, Letterbox640x480(), 640, 480, 0.5);

        var single = Assert.Single(got);
        Assert.Equal(0.45, single.Box.X, precision: 12);
        Assert.Equal(0.45, single.Box.Y, precision: 12);
        Assert.Equal(0.1, single.Box.W, precision: 12);
        Assert.Equal(0.1, single.Box.H, precision: 12);
        Assert.Equal(1.0, single.Score, precision: 12);
    }

    [Fact]
    public void Parse_ObjTimesClsGating_BoundaryKept()
    {
        var output = BlankOutput(2);
        SetAnchor(output, 0, 320f, 320f, 64f, 64f, obj: 0.5f, personCls: 1f); // score exactly 0.5
        SetAnchor(output, 1, 320f, 320f, 64f, 64f, obj: 0.25f, personCls: 1f); // score 0.25

        var parser = new YoloxOutputParser();
        var got = parser.Parse(output, new[] { 1, 2, 85 }, Letterbox640x480(), 640, 480, 0.5);

        var single = Assert.Single(got);
        Assert.Equal(0.5, single.Score, precision: 12);
    }

    [Fact]
    public void Parse_PartialPadOverflow_Clamped()
    {
        // Canvas y 70..110 straddles the image top (padTop 80): source y clamps to 0.
        var output = BlankOutput(1);
        SetAnchor(output, 0, 320f, 90f, 640f, 40f, obj: 1f, personCls: 1f);

        var parser = new YoloxOutputParser();
        var got = parser.Parse(output, new[] { 1, 1, 85 }, Letterbox640x480(), 640, 480, 0.5);

        var single = Assert.Single(got);
        Assert.Equal(0.0, single.Box.X, precision: 12);
        Assert.Equal(0.0, single.Box.Y, precision: 12);
        Assert.Equal(1.0, single.Box.W, precision: 12);
        Assert.Equal(30.0 / 480.0, single.Box.H, precision: 12);
    }

    [Fact]
    public void Parse_FullyInPad_Dropped()
    {
        // Canvas y 30..50 lies entirely in the top pad: nothing survives the clamp.
        var output = BlankOutput(2);
        SetAnchor(output, 0, 320f, 40f, 64f, 20f, obj: 1f, personCls: 1f);
        SetAnchor(output, 1, 320f, 320f, 64f, 64f, obj: 1f, personCls: 1f);

        var parser = new YoloxOutputParser();
        var got = parser.Parse(output, new[] { 1, 2, 85 }, Letterbox640x480(), 640, 480, 0.5);

        Assert.Single(got); // only anchor 1 survives
    }

    [Fact]
    public void Parse_NonFiniteAnchors_SkippedNeverThrows()
    {
        var output = BlankOutput(4);
        SetAnchor(output, 0, float.NaN, 320f, 64f, 64f, obj: 1f, personCls: 1f);
        SetAnchor(output, 1, 320f, 320f, 64f, 64f, obj: float.PositiveInfinity, personCls: 1f);
        SetAnchor(output, 2, 320f, 320f, 64f, 64f, obj: 1f, personCls: float.NaN);
        SetAnchor(output, 3, 320f, 320f, 64f, 64f, obj: 1f, personCls: 1f);

        var parser = new YoloxOutputParser();
        var got = parser.Parse(output, new[] { 1, 4, 85 }, Letterbox640x480(), 640, 480, 0.5);

        Assert.Single(got);
        Assert.Equal(1.0, got[0].Score, precision: 12);
    }

    [Theory]
    [InlineData(new int[] { 1, 4, 64 })] // wrong channel count
    [InlineData(new int[] { 4, 85 })] // rank 2
    [InlineData(new int[] { 2, 4, 85 })] // batch != 1
    [InlineData(new int[] { 1, 0, 85 })] // no anchors
    public void Parse_BadShape_Throws(int[] dims)
    {
        var parser = new YoloxOutputParser();
        Assert.Throws<InvalidDataException>(() =>
            parser.Parse(new float[4 * 85], dims, Letterbox640x480(), 640, 480, 0.5));
    }

    [Fact]
    public void Parse_LengthMismatch_Throws()
    {
        var parser = new YoloxOutputParser();
        Assert.Throws<InvalidDataException>(() =>
            parser.Parse(new float[(4 * 85) - 1], new[] { 1, 4, 85 }, Letterbox640x480(), 640, 480, 0.5));
    }

    [Fact]
    public void Parse_Non8400AnchorCount_Accepted()
    {
        // Stride-variant exports (different grid split) decode through the same rows.
        var output = BlankOutput(100);
        SetAnchor(output, 50, 320f, 320f, 64f, 64f, obj: 1f, personCls: 1f);

        var parser = new YoloxOutputParser();
        var got = parser.Parse(output, new[] { 1, 100, 85 }, Letterbox640x480(), 640, 480, 0.5);

        Assert.Single(got);
    }

    [Fact]
    public void Parse_TopK_IsScoreOrderedNotAnchorOrdered()
    {
        // Scores ascend with anchor index; cap 2 must return anchors 4, 3 (not 0, 1).
        var output = BlankOutput(5);
        for (var a = 0; a < 5; a++)
        {
            SetAnchor(output, a, 320f, 320f, 64f, 64f, obj: 1f, personCls: 0.5f + (0.1f * a));
        }

        var parser = new YoloxOutputParser(maxCandidates: 2);
        var got = parser.Parse(output, new[] { 1, 5, 85 }, Letterbox640x480(), 640, 480, 0.0);

        Assert.Equal(2, got.Count);
        Assert.True(got[0].Score > got[1].Score);
        Assert.Equal(0.9, got[0].Score, precision: 6);
        Assert.Equal(0.8, got[1].Score, precision: 6);
    }

    [Fact]
    public void Parse_NonPersonArgmax_Dropped()
    {
        var output = BlankOutput(2);
        // Anchor 0: person score alone would pass (0.8), but class 1 wins (0.9).
        SetAnchor(output, 0, 320f, 320f, 64f, 64f, obj: 1f, personCls: 0.8f, otherCls: 0.9f);
        // Anchor 1: person is the argmax.
        SetAnchor(output, 1, 100f, 200f, 64f, 64f, obj: 1f, personCls: 0.9f, otherCls: 0.1f);

        var parser = new YoloxOutputParser();
        var got = parser.Parse(output, new[] { 1, 2, 85 }, Letterbox640x480(), 640, 480, 0.5);

        var single = Assert.Single(got);
        Assert.Equal(0.9, single.Score, precision: 6); // float-derived score: 6, like its siblings
    }

    [Fact]
    public void Parser_Defaults_Documented()
    {
        var parser = new YoloxOutputParser();
        Assert.Equal(0, parser.PersonClassIndex);
        Assert.Equal(YoloxOutputParser.DefaultMaxCandidates, parser.MaxCandidates);
        Assert.Equal(1000, YoloxOutputParser.DefaultMaxCandidates);
        Assert.Equal(640, YoloxOutputParser.SupportedInputSize);
    }

    [Theory]
    [InlineData(-1, 1000)]
    [InlineData(80, 1000)]
    [InlineData(0, 0)]
    public void Parser_BadCtorArgs_Throw(int personClassIndex, int maxCandidates)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new YoloxOutputParser(personClassIndex, maxCandidates));
    }
}
