using EduSphere.Safety.Edge;
using Xunit;

public sealed class EdgeGeometryTests
{
    private static PolygonZone Square() => new(new[]
    {
        new TrackedPoint(0.2, 0.2),
        new TrackedPoint(0.8, 0.2),
        new TrackedPoint(0.8, 0.8),
        new TrackedPoint(0.2, 0.8),
    });

    private static DirectedBoundary VerticalBoundary() =>
        new(new TrackedPoint(0.6, 0.1), new TrackedPoint(0.6, 0.9));

    [Fact]
    public void Contains_InsideSquare_True()
    {
        Assert.True(Square().Contains(new TrackedPoint(0.5, 0.5)));
    }

    [Fact]
    public void Contains_OutsideSquare_False()
    {
        Assert.False(Square().Contains(new TrackedPoint(0.05, 0.5)));
        Assert.False(Square().Contains(new TrackedPoint(0.5, 0.95)));
    }

    [Fact]
    public void Contains_VertexBoundary_Inside()
    {
        Assert.True(Square().Contains(new TrackedPoint(0.2, 0.2)));
    }

    [Fact]
    public void Contains_EdgeMidpointBoundary_Inside()
    {
        Assert.True(Square().Contains(new TrackedPoint(0.5, 0.2)));
        Assert.True(Square().Contains(new TrackedPoint(0.2, 0.55)));
    }

    [Fact]
    public void Contains_FootPointMarginallyOutsideOne_Tolerated()
    {
        // Tolerance slack from BoundingBox conversion must not throw and must classify.
        var zone = new PolygonZone(new[]
        {
            new TrackedPoint(0.0, 0.0),
            new TrackedPoint(1.0, 0.0),
            new TrackedPoint(1.0, 1.0),
            new TrackedPoint(0.0, 1.0),
        });
        Assert.True(zone.Contains(new TrackedPoint(0.5, 1.0 + 5e-10)));
    }

    [Fact]
    public void ContainsWithMargin_JustOutsideWithinMargin_True()
    {
        Assert.True(Square().ContainsWithMargin(new TrackedPoint(0.82, 0.5), 0.05));
    }

    [Fact]
    public void ContainsWithMargin_OutsideBeyondMargin_False()
    {
        Assert.False(Square().ContainsWithMargin(new TrackedPoint(0.9, 0.5), 0.05));
    }

    [Fact]
    public void ContainsWithMargin_ZeroMargin_EqualsContains()
    {
        var zone = Square();
        var inside = new TrackedPoint(0.5, 0.5);
        var outside = new TrackedPoint(0.9, 0.5);
        Assert.Equal(zone.Contains(inside), zone.ContainsWithMargin(inside, 0.0));
        Assert.Equal(zone.Contains(outside), zone.ContainsWithMargin(outside, 0.0));
    }

    [Fact]
    public void ContainsWithMargin_NegativeMargin_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Square().ContainsWithMargin(new TrackedPoint(0.5, 0.5), -0.01));
    }

    [Fact]
    public void PolygonZone_FewerThanThreeVertices_Throws()
    {
        Assert.Throws<ArgumentException>(() => new PolygonZone(new[]
        {
            new TrackedPoint(0.1, 0.1),
            new TrackedPoint(0.9, 0.9),
        }));
    }

    [Fact]
    public void DirectedBoundary_DegenerateEndpoints_Throws()
    {
        var p = new TrackedPoint(0.5, 0.5);
        Assert.Throws<ArgumentException>(() => new DirectedBoundary(p, new TrackedPoint(0.5, 0.5)));
    }

    private static List<TrackedPoint> Trail(params (double X, double Y)[] pts) =>
        pts.Select(p => new TrackedPoint(p.X, p.Y)).ToList();

    [Fact]
    public void CrossedSide_LeftToRightCrossing_ReturnsNegativeOne()
    {
        // Upward boundary (0,+) vs rightward movement: cross < 0.
        var side = VerticalBoundary().CrossedSide(Trail((0.3, 0.5), (0.5, 0.5), (0.8, 0.5)));
        Assert.Equal(-1, side);
    }

    [Fact]
    public void CrossedSide_RightToLeftCrossing_ReturnsPositiveOne()
    {
        var side = VerticalBoundary().CrossedSide(Trail((0.8, 0.5), (0.6, 0.5), (0.3, 0.5)));
        Assert.Equal(1, side);
    }

    [Fact]
    public void CrossedSide_FewerThanThreePoints_ReturnsZero()
    {
        Assert.Equal(0, VerticalBoundary().CrossedSide(Trail((0.3, 0.5), (0.8, 0.5))));
    }

    [Fact]
    public void CrossedSide_ParallelMotionNearBoundary_ReturnsZero()
    {
        // Moves parallel to the boundary on one side: no intersection.
        var side = VerticalBoundary().CrossedSide(Trail((0.3, 0.3), (0.3, 0.5), (0.3, 0.7)));
        Assert.Equal(0, side);
    }

    [Fact]
    public void CrossedSide_CrossingLineOutsideSegmentBounds_ReturnsZero()
    {
        // Crosses x=0.6 far below the segment (y=0.02 < 0.1).
        var side = VerticalBoundary().CrossedSide(Trail((0.3, 0.02), (0.5, 0.02), (0.8, 0.02)));
        Assert.Equal(0, side);
    }

    [Fact]
    public void CrossedSide_ReversingSteps_ReturnsZero()
    {
        // Steps disagree in sign: out-and-back jitter, not a sustained crossing.
        var side = VerticalBoundary().CrossedSide(Trail((0.3, 0.5), (0.8, 0.5), (0.3, 0.5)));
        Assert.Equal(0, side);
    }

    [Fact]
    public void CrossedSide_StationaryLastStep_ReturnsZero()
    {
        var side = VerticalBoundary().CrossedSide(Trail((0.3, 0.5), (0.8, 0.5), (0.8, 0.5)));
        Assert.Equal(0, side);
    }
}
