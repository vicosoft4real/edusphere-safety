using EduSphere.Safety.Edge;
using Xunit;

public sealed class EdgeTrackerTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 1, 15, 3, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(long frame) => T0.AddSeconds(frame);

    private static Detection Det(double x, double y, double w, double h, double confidence = 0.9)
        => new(new BoundingBox(x, y, w, h), confidence, Detection.PersonClassId);

    private static Detection BoxA() => Det(0.1, 0.1, 0.3, 0.3);

    [Fact]
    public void IouThreshold_IsDocumentedValue()
    {
        Assert.Equal(0.3, Tracker.IouThreshold);
    }

    [Fact]
    public void GraceFrames_ZeroOrNegative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Tracker(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Tracker(-1));
    }

    [Fact]
    public void SameBox_ConsecutiveFrames_KeepsSameId()
    {
        var tracker = new Tracker();
        var first = tracker.Update(0, new[] { BoxA() }, At(0), false);
        var second = tracker.Update(1, new[] { BoxA() }, At(1), false);

        Assert.Single(first.ActiveTracks);
        Assert.Single(second.ActiveTracks);
        Assert.Equal(first.ActiveTracks[0].Id, second.ActiveTracks[0].Id);
        Assert.Equal(1L, first.ActiveTracks[0].Id);
    }

    [Fact]
    public void TwoBoxes_KeepDistinctIdsAcrossFrames()
    {
        var tracker = new Tracker();
        var dets = new[] { Det(0.05, 0.1, 0.2, 0.3), Det(0.65, 0.1, 0.2, 0.3) };
        var first = tracker.Update(0, dets, At(0), false);
        var second = tracker.Update(1, dets, At(1), false);

        Assert.Equal(2, first.ActiveTracks.Count);
        Assert.NotEqual(first.ActiveTracks[0].Id, first.ActiveTracks[1].Id);
        var firstIds = first.ActiveTracks.Select(t => t.Id).OrderBy(i => i).ToArray();
        var secondIds = second.ActiveTracks.Select(t => t.Id).OrderBy(i => i).ToArray();
        Assert.Equal(firstIds, secondIds);
    }

    [Fact]
    public void BriefDisappearance_BelowGrace_KeepsId()
    {
        var tracker = new Tracker(graceFrames: 3);
        var first = tracker.Update(0, new[] { BoxA() }, At(0), false);
        tracker.Update(1, Array.Empty<Detection>(), At(1), false);
        tracker.Update(2, Array.Empty<Detection>(), At(2), false);
        var back = tracker.Update(3, new[] { BoxA() }, At(3), false);

        Assert.Single(back.ActiveTracks);
        Assert.Equal(first.ActiveTracks[0].Id, back.ActiveTracks[0].Id);
        Assert.Equal(0, back.ActiveTracks[0].Misses);
    }

    [Fact]
    public void Disappearance_EqualToGrace_RetiresId_Boundary()
    {
        var tracker = new Tracker(graceFrames: 3);
        var first = tracker.Update(0, new[] { BoxA() }, At(0), false);
        tracker.Update(1, Array.Empty<Detection>(), At(1), false);
        tracker.Update(2, Array.Empty<Detection>(), At(2), false);
        var third = tracker.Update(3, Array.Empty<Detection>(), At(3), false);

        Assert.Empty(third.ActiveTracks);

        var reentry = tracker.Update(4, new[] { BoxA() }, At(4), false);
        Assert.Single(reentry.ActiveTracks);
        Assert.NotEqual(first.ActiveTracks[0].Id, reentry.ActiveTracks[0].Id);
    }

    [Fact]
    public void Reentry_AfterRetire_GetsNewId()
    {
        var tracker = new Tracker(graceFrames: 1);
        var first = tracker.Update(0, new[] { BoxA() }, At(0), false);
        var empty = tracker.Update(1, Array.Empty<Detection>(), At(1), false);

        Assert.Empty(empty.ActiveTracks);

        var reentry = tracker.Update(2, new[] { BoxA() }, At(2), false);
        Assert.Single(reentry.ActiveTracks);
        Assert.NotEqual(first.ActiveTracks[0].Id, reentry.ActiveTracks[0].Id);
    }

    [Fact]
    public void Ids_NeverReusedWithinRun()
    {
        var tracker = new Tracker(graceFrames: 1);
        var seen = new HashSet<long>();
        for (long f = 0; f < 6; f++)
        {
            var result = f % 2 == 0
                ? tracker.Update(f, new[] { BoxA() }, At(f), false)
                : tracker.Update(f, Array.Empty<Detection>(), At(f), false);
            foreach (var track in result.ActiveTracks)
            {
                Assert.DoesNotContain(track.Id, seen);
                seen.Add(track.Id);
            }
        }

        Assert.Equal(new HashSet<long> { 1, 2, 3 }, seen);
    }

    [Fact]
    public void ResolutionChange_RetiresAllTracks()
    {
        var tracker = new Tracker();
        var dets = new[] { Det(0.05, 0.1, 0.2, 0.3), Det(0.65, 0.1, 0.2, 0.3) };
        var first = tracker.Update(0, dets, At(0), false);
        var oldIds = first.ActiveTracks.Select(t => t.Id).ToHashSet();

        var after = tracker.Update(1, dets, At(1), isResolutionChange: true);

        Assert.Equal(2, after.ActiveTracks.Count);
        Assert.All(after.ActiveTracks, t => Assert.DoesNotContain(t.Id, oldIds));
    }

    [Fact]
    public void EmptyDetections_RetireGradually_MissesAccumulate()
    {
        var tracker = new Tracker(graceFrames: 3);
        tracker.Update(0, new[] { BoxA() }, At(0), false);

        // NOTE: TrackedFrame holds live Track references, so each frame's Misses
        // must be asserted before the next Update mutates the same track.
        var miss1 = tracker.Update(1, Array.Empty<Detection>(), At(1), false);
        Assert.Single(miss1.ActiveTracks);
        Assert.Equal(1, miss1.ActiveTracks[0].Misses);

        var miss2 = tracker.Update(2, Array.Empty<Detection>(), At(2), false);
        Assert.Single(miss2.ActiveTracks);
        Assert.Equal(2, miss2.ActiveTracks[0].Misses);

        var miss3 = tracker.Update(3, Array.Empty<Detection>(), At(3), false);
        Assert.Empty(miss3.ActiveTracks);
    }

    [Fact]
    public void GroupSplit_BestIouKeepsId_OtherIsNew()
    {
        var tracker = new Tracker();
        var parent = Det(0.2, 0.2, 0.4, 0.4);
        var first = tracker.Update(0, new[] { parent }, At(0), false);
        var parentId = first.ActiveTracks[0].Id;

        var split = tracker.Update(1, new[] { parent, Det(0.5, 0.5, 0.3, 0.3) }, At(1), false);

        Assert.Equal(2, split.ActiveTracks.Count);
        var keeper = split.ActiveTracks.Single(t => t.Id == parentId);
        Assert.Equal(parent.Box, keeper.Box);
        Assert.Single(split.ActiveTracks, t => t.Id != parentId);
    }

    [Fact]
    public void DisjointBoxes_NeverMatch()
    {
        var tracker = new Tracker();
        var first = tracker.Update(0, new[] { Det(0.0, 0.0, 0.2, 0.2) }, At(0), false);
        var oldId = first.ActiveTracks[0].Id;

        var second = tracker.Update(1, new[] { Det(0.7, 0.7, 0.2, 0.2) }, At(1), false);

        Assert.Equal(2, second.ActiveTracks.Count);
        var newcomer = second.ActiveTracks.Single(t => t.Id != oldId);
        Assert.Equal(0, newcomer.Misses);
        var survivor = second.ActiveTracks.Single(t => t.Id == oldId);
        Assert.Equal(1, survivor.Misses);
    }

    [Fact]
    public void MatchedTrack_UpdatesBoxAndTrail()
    {
        var tracker = new Tracker();
        tracker.Update(0, new[] { BoxA() }, At(0), false);
        var moved = Det(0.15, 0.1, 0.3, 0.3);
        var second = tracker.Update(1, new[] { moved }, At(1), false);

        Assert.Single(second.ActiveTracks);
        Assert.Equal(1L, second.ActiveTracks[0].Id);
        Assert.Equal(moved.Box, second.ActiveTracks[0].Box);
        Assert.Equal(2, second.ActiveTracks[0].FootPointTrail.Count);
    }

    [Fact]
    public void FootPointTrail_IsBounded()
    {
        var tracker = new Tracker();
        TrackedFrame last = tracker.Update(0, new[] { BoxA() }, At(0), false);
        for (long f = 1; f < 40; f++)
        {
            last = tracker.Update(f, new[] { BoxA() }, At(f), false);
        }

        Assert.Single(last.ActiveTracks);
        Assert.Equal(Tracker.MaxTrailLength, last.ActiveTracks[0].FootPointTrail.Count);
    }

    [Fact]
    public void Track_CarriesConfidenceAndLastDetectionTimeUtc_OnConstruction()
    {
        var at = T0.AddSeconds(7);
        var track = new Track(1, BoxA().Box, 7, 0.72, at);

        Assert.Equal(0.72, track.Confidence);
        Assert.Equal(at, track.LastDetectionTimeUtc);
    }

    [Fact]
    public void Track_InvalidConfidence_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Track(1, BoxA().Box, 0, double.NaN, T0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Track(1, BoxA().Box, 0, -0.5, T0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Track(1, BoxA().Box, 0, 1.5, T0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Track(1, BoxA().Box, 0, double.PositiveInfinity, T0));
    }

    [Fact]
    public void Update_NewTrack_TakesDetectionConfidenceAndFrameTime()
    {
        var tracker = new Tracker();
        var at = At(0);
        var frame = tracker.Update(0, new[] { Det(0.1, 0.1, 0.3, 0.3, confidence: 0.62) }, at, false);

        var track = Assert.Single(frame.ActiveTracks);
        Assert.Equal(0.62, track.Confidence);
        Assert.Equal(at, track.LastDetectionTimeUtc);
    }

    [Fact]
    public void Update_Match_RefreshesConfidenceAndDetectionTime()
    {
        var tracker = new Tracker();
        tracker.Update(0, new[] { Det(0.1, 0.1, 0.3, 0.3, confidence: 0.62) }, At(0), false);

        // Same box (IoU 1.0) with a different detector confidence one frame later.
        var frame = tracker.Update(1, new[] { Det(0.1, 0.1, 0.3, 0.3, confidence: 0.87) }, At(1), false);

        var track = Assert.Single(frame.ActiveTracks);
        Assert.Equal(0.87, track.Confidence);
        Assert.Equal(At(1), track.LastDetectionTimeUtc);
    }

    [Fact]
    public void Update_DetectionsInOneFrame_ShareFrameTime()
    {
        var tracker = new Tracker();
        var at = At(3);
        var frame = tracker.Update(
            3,
            new[] { Det(0.05, 0.1, 0.2, 0.3, confidence: 0.7), Det(0.65, 0.1, 0.2, 0.3, confidence: 0.8) },
            at,
            false);

        Assert.Equal(2, frame.ActiveTracks.Count);
        // One shared frame event time; per-detection confidence still differs.
        Assert.All(frame.ActiveTracks, t => Assert.Equal(at, t.LastDetectionTimeUtc));
        Assert.Equal(
            new[] { 0.7, 0.8 },
            frame.ActiveTracks.OrderBy(t => t.Id).Select(t => t.Confidence).ToArray());
    }
}
