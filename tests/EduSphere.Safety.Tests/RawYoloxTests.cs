using EduSphere.Safety.Edge;
using Xunit;

public sealed class RawYoloxTests
{
    [Theory]
    [InlineData(8, 0, 0, 0)]
    [InlineData(16, 2704, 4, 3)]
    [InlineData(32, 3380, 2, 1)]
    public void RawGrid_PersonBoxDecodesAtEachStride(int stride, int levelStart, int x, int y)
    {
        // A regression in stride/grid decoding would move or discard this real model's boxes.
        const int size = 416, anchors = 3549;
        var raw = new float[anchors * 85];
        var anchor = levelStart + y * (size / stride) + x;
        var offset = anchor * 85;
        raw[offset] = 0.5f; raw[offset + 1] = 0.75f;
        raw[offset + 2] = MathF.Log(0.5f); raw[offset + 3] = MathF.Log(0.5f);
        raw[offset + 4] = 1; raw[offset + 5] = 0.9f;
        var parser = new YoloxOutputParser(rawGridInputSize: size);
        var box = Assert.Single(parser.Parse(raw, [1, anchors, 85], Letterbox.Compute(size, size, size, size), size, size, 0.5)).Box;
        Assert.Equal((x + 0.25) * stride / size, box.X, 6);
        Assert.Equal((y + 0.5) * stride / size, box.Y, 6);
        Assert.Equal(0.5 * stride / size, box.W, 6);
        Assert.Equal(0.5 * stride / size, box.H, 6);
    }

    [Fact]
    public void RawGrid_RejectsWrongAnchorCount()
    {
        var parser = new YoloxOutputParser(rawGridInputSize: 416);
        Assert.Throws<InvalidDataException>(() => parser.Parse(new float[85], [1, 1, 85],
            Letterbox.Compute(416, 416, 416, 416), 416, 416, 0.5));
    }
}
