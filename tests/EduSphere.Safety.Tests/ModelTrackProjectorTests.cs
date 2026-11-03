using EduSphere.Safety.Edge;
using Xunit;

public sealed class ModelTrackProjectorTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.UnixEpoch;
    private static readonly BoundingBox Left = new(0.1, 0.2, 0.1, 0.3);
    private static readonly BoundingBox Right = new(0.6, 0.2, 0.1, 0.3);

    [Fact]
    public void ModelIdsSurviveBoxCrossingAndRecordReordering()
    {
        var projector = new ModelTrackProjector();
        projector.Update(0, [new(0, Left, 0.9), new(42, Right, 0.8)], T);
        var result = projector.Update(1, [new(42, Left, 0.8), new(0, Right, 0.9)], T.AddSeconds(1));
        // SAM id 0 maps to core id 1; id 42 maps to 43. Geometric reassignment is wrong.
        Assert.Equal(Right, Assert.Single(result.ActiveTracks, t => t.Id == 1).Box);
        Assert.Equal(Left, Assert.Single(result.ActiveTracks, t => t.Id == 43).Box);
        Assert.Equal(2, result.ActiveTracks[0].FootPointTrail.Count);
    }

    [Fact]
    public void MissingFrameClearsPresenceAndDoesNotBridgeItsTrail()
    {
        var projector = new ModelTrackProjector();
        projector.Update(0, [new(7, Left, 0.9)], T);
        Assert.Empty(projector.Update(1, [], T.AddSeconds(1)).ActiveTracks);
        var returned = Assert.Single(projector.Update(2, [new(7, Right, 0.9)], T.AddSeconds(2)).ActiveTracks);
        Assert.Equal(8, returned.Id);
        Assert.Single(returned.FootPointTrail);
    }

    [Fact]
    public void DuplicateIdsAndNonIncreasingFramesAreRejected()
    {
        var projector = new ModelTrackProjector();
        projector.Update(0, [], T);
        Assert.Throws<ArgumentException>(() => projector.Update(1, [new(7, Left, 0.9), new(7, Right, 0.9)], T));
        Assert.Throws<ArgumentOutOfRangeException>(() => projector.Update(0, [], T));
    }
}
