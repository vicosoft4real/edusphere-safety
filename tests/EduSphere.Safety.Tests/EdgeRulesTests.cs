using EduSphere.Safety.Edge;
using Xunit;

public sealed class EdgeRulesTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 1, 15, 3, 0, 0, TimeSpan.Zero); // 22:00 EST previous day -> closed

    private static PolygonZone Square() => new(new[]
    {
        new TrackedPoint(0.2, 0.2),
        new TrackedPoint(0.8, 0.2),
        new TrackedPoint(0.8, 0.8),
        new TrackedPoint(0.2, 0.8),
    });

    private static Schedule ClosedNightSchedule() => new(
        "America/New_York",
        new[] { new DailyWindow(new TimeOnly(22, 0), new TimeOnly(6, 0)) });

    private static Dictionary<string, Schedule> Schedules() =>
        new(StringComparer.Ordinal) { ["night"] = ClosedNightSchedule() };

    private static Track InsideTrack(long id, long frame) =>
        new(id, new BoundingBox(0.4, 0.4, 0.2, 0.2), frame, 0.9, T0.AddSeconds(frame)); // foot (0.5, 0.6): inside

    private static Track OutsideTrack(long id, long frame) =>
        new(id, new BoundingBox(0.01, 0.01, 0.1, 0.1), frame, 0.9, T0.AddSeconds(frame)); // foot (0.06, 0.11): outside

    private static ScoredTrack Score(Track t, DateTimeOffset at, double confidence = 0.9) =>
        new(t, confidence, at);

    /// <summary>
    /// Task 5 handoff pattern: score straight from the tracker's own output — the
    /// track's carried confidence + detection time, unfiltered ActiveTracks.
    /// </summary>
    private static IReadOnlyList<ScoredTrack> ScoreFromTracks(TrackedFrame frame) =>
        frame.ActiveTracks.Select(t => new ScoredTrack(t, t.Confidence, t.LastDetectionTimeUtc)).ToList();

    private static RuleConfig RuleA(string id = "a", int version = 1, string? scheduleId = "night") =>
        new(id, RuleType.ZoneIntrusion, version, persistenceFrames: 3, polygon: Square(), scheduleId: scheduleId);

    private static IReadOnlyList<RuleEvent> Eval(
        RuleEngine engine, DateTimeOffset at, string model, params ScoredTrack[] tracks) =>
        engine.Evaluate(at, tracks, model);

    private static BoundingBox CenterBox(double centerX) =>
        new(centerX - 0.125, 0.2, 0.25, 0.3); // foot (centerX, 0.5)

    private static DateTimeOffset[] Times(DateTimeOffset start, int count) =>
        Enumerable.Range(0, count).Select(i => start.AddSeconds(i)).ToArray();

    private static void DriveLeg(
        Tracker tracker, RuleEngine engine, double[] centers, DateTimeOffset[] times,
        List<RuleEvent> sink, ref long frame)
    {
        Assert.Equal(centers.Length, times.Length);
        for (var i = 0; i < centers.Length; i++)
        {
            var tracked = tracker.Update(
                frame,
                new[] { new Detection(CenterBox(centers[i]), 0.9, Detection.PersonClassId) },
                times[i],
                false);
            frame++;
            Assert.Single(tracked.ActiveTracks);
            sink.AddRange(engine.Evaluate(times[i], ScoreFromTracks(tracked), "m1"));
        }
    }

    [Fact]
    public void ZoneIntrusion_FiresAfterPersistenceFrames_WhenClosed()
    {
        var engine = new RuleEngine(new[] { RuleA() }, schedulesById: Schedules());
        Assert.Empty(Eval(engine, T0, "m1", Score(InsideTrack(1, 0), T0)));
        Assert.Empty(Eval(engine, T0.AddSeconds(1), "m1", Score(InsideTrack(1, 1), T0.AddSeconds(1))));
        var fired = Eval(engine, T0.AddSeconds(2), "m1", Score(InsideTrack(1, 2), T0.AddSeconds(2)));

        var e = Assert.Single(fired);
        Assert.Equal("a", e.RuleId);
        Assert.Equal(1, e.RuleVersion);
        Assert.Equal(1L, e.TrackId);
        Assert.Equal(T0.AddSeconds(2), e.EventTimeUtc);
        Assert.Equal(T0.AddSeconds(2), e.DetectionTimeUtc);
        Assert.Equal(T0.AddSeconds(2), e.IngestTimeUtc);
        Assert.Equal("m1", e.ModelVersion);
        Assert.Null(e.SupersedesEventId);
        Assert.NotEmpty(e.FootTrailSnapshot);
    }

    [Fact]
    public void ZoneIntrusion_NoScheduleGate_AlwaysArmed()
    {
        var engine = new RuleEngine(new[] { RuleA(scheduleId: null) });
        Eval(engine, T0, "m1", Score(InsideTrack(1, 0), T0));
        Eval(engine, T0.AddSeconds(1), "m1", Score(InsideTrack(1, 1), T0.AddSeconds(1)));
        var fired = Eval(engine, T0.AddSeconds(2), "m1", Score(InsideTrack(1, 2), T0.AddSeconds(2)));
        Assert.Single(fired);
    }

    [Fact]
    public void ZoneIntrusion_SilentWhileScheduleOpen()
    {
        // T0 + 12h = 15:00 UTC = 10:00 EST, inside school hours -> open.
        var engine = new RuleEngine(new[] { RuleA() }, schedulesById: Schedules());
        var open = T0.AddHours(12);
        for (var i = 0; i < 5; i++)
        {
            Assert.Empty(Eval(engine, open.AddSeconds(i), "m1", Score(InsideTrack(1, i), open.AddSeconds(i))));
        }
    }

    [Fact]
    public void ZoneIntrusion_UnknownScheduleId_Throws()
    {
        var engine = new RuleEngine(new[] { RuleA(scheduleId: "missing") }, schedulesById: Schedules());
        Assert.Throws<InvalidOperationException>(
            () => Eval(engine, T0, "m1", Score(InsideTrack(1, 0), T0)));
    }

    [Fact]
    public void ZoneIntrusion_FarExitResetsEpisode()
    {
        var engine = new RuleEngine(new[] { RuleA() }, schedulesById: Schedules());
        for (var i = 0; i < 3; i++)
        {
            Eval(engine, T0.AddSeconds(i), "m1", Score(InsideTrack(1, i), T0.AddSeconds(i)));
        }

        // Fully outside the zone and its (zero) margin: episode ends, rule re-arms.
        Assert.Empty(Eval(engine, T0.AddSeconds(3), "m1", Score(OutsideTrack(1, 3), T0.AddSeconds(3))));
        Assert.Empty(Eval(engine, T0.AddSeconds(4), "m1", Score(InsideTrack(1, 4), T0.AddSeconds(4))));
        Assert.Empty(Eval(engine, T0.AddSeconds(5), "m1", Score(InsideTrack(1, 5), T0.AddSeconds(5))));
        var refired = Eval(engine, T0.AddSeconds(6), "m1", Score(InsideTrack(1, 6), T0.AddSeconds(6)));
        Assert.Single(refired);
    }

    [Fact]
    public void ZoneIntrusion_MarginHysteresisHoldsCount()
    {
        var rule = new RuleConfig(
            "a", RuleType.ZoneIntrusion, 1, persistenceFrames: 3, exitMargin: 0.05,
            polygon: Square(), scheduleId: "night");
        var engine = new RuleEngine(new[] { rule }, schedulesById: Schedules());
        Eval(engine, T0, "m1", Score(InsideTrack(1, 0), T0));
        Eval(engine, T0.AddSeconds(1), "m1", Score(InsideTrack(1, 1), T0.AddSeconds(1)));

        // Foot at x=0.83: outside Contains, but inside the 0.05 margin band -> count held.
        var edge = new Track(1, new BoundingBox(0.73, 0.4, 0.2, 0.2), 2, 0.9, T0.AddSeconds(2)); // foot (0.83, 0.6)
        Assert.Empty(Eval(engine, T0.AddSeconds(2), "m1", Score(edge, T0.AddSeconds(2))));

        var fired = Eval(engine, T0.AddSeconds(3), "m1", Score(InsideTrack(1, 3), T0.AddSeconds(3)));
        Assert.Single(fired);
    }

    [Fact]
    public void AbsentOneFrame_ResetsZoneEpisode()
    {
        var engine = new RuleEngine(new[] { RuleA(scheduleId: null) });
        Eval(engine, T0, "m1", Score(InsideTrack(1, 0), T0));
        Eval(engine, T0.AddSeconds(1), "m1", Score(InsideTrack(1, 1), T0.AddSeconds(1)));

        // One frame with no tracks at all: the episode drops (tracker grace-expiry equivalent).
        Assert.Empty(engine.Evaluate(T0.AddSeconds(2), Array.Empty<ScoredTrack>(), "m1"));

        // Counting restarts from scratch: two more frames are not enough.
        Assert.Empty(Eval(engine, T0.AddSeconds(3), "m1", Score(InsideTrack(1, 4), T0.AddSeconds(3))));
        Assert.Empty(Eval(engine, T0.AddSeconds(4), "m1", Score(InsideTrack(1, 5), T0.AddSeconds(4))));
        Assert.Single(Eval(engine, T0.AddSeconds(5), "m1", Score(InsideTrack(1, 6), T0.AddSeconds(5))));
    }

    [Fact]
    public void AbsentOneFrame_ResetsDwellClock()
    {
        var rule = new RuleConfig(
            "c", RuleType.Dwell, 1, dwellSeconds: 5.0, polygon: Square(), scheduleId: null);
        var engine = new RuleEngine(new[] { rule });
        Eval(engine, T0, "m1", Score(InsideTrack(1, 0), T0));
        Eval(engine, T0.AddSeconds(4), "m1", Score(InsideTrack(1, 1), T0.AddSeconds(4)));

        Assert.Empty(engine.Evaluate(T0.AddSeconds(5), Array.Empty<ScoredTrack>(), "m1"));

        // PresenceStart restarts at t=6: t=10 is only 4s in, t=11 fires.
        Assert.Empty(Eval(engine, T0.AddSeconds(6), "m1", Score(InsideTrack(1, 3), T0.AddSeconds(6))));
        Assert.Empty(Eval(engine, T0.AddSeconds(10), "m1", Score(InsideTrack(1, 4), T0.AddSeconds(10))));
        Assert.Single(Eval(engine, T0.AddSeconds(11), "m1", Score(InsideTrack(1, 5), T0.AddSeconds(11))));
    }

    [Fact]
    public void BoundaryCrossing_ExpectedDirection_Fires()
    {
        var boundary = new DirectedBoundary(new TrackedPoint(0.6, 0.1), new TrackedPoint(0.6, 0.9));
        var rule = new RuleConfig("b", RuleType.BoundaryCrossing, 1, boundary: boundary, direction: -1);
        var engine = new RuleEngine(new[] { rule });
        var tracker = new Tracker(graceFrames: 10);
        IReadOnlyList<RuleEvent> fired = Array.Empty<RuleEvent>();
        var centers = new[] { 0.30, 0.40, 0.50, 0.60, 0.70 };
        for (var i = 0; i < centers.Length; i++)
        {
            var at = T0.AddSeconds(i);
            var frame = tracker.Update(i, new[] { new Detection(CenterBox(centers[i]), 0.9, Detection.PersonClassId) }, at, false);
            fired = engine.Evaluate(at, frame.ActiveTracks.Select(t => new ScoredTrack(t, 0.9, at)).ToList(), "m1");
            if (fired.Count > 0)
            {
                break;
            }
        }

        var e = Assert.Single(fired);
        Assert.Equal("b", e.RuleId);
        Assert.Equal(-1, rule.Direction);
    }

    [Fact]
    public void BoundaryCrossing_WrongDirection_Silent()
    {
        var boundary = new DirectedBoundary(new TrackedPoint(0.6, 0.1), new TrackedPoint(0.6, 0.9));
        var rule = new RuleConfig("b", RuleType.BoundaryCrossing, 1, boundary: boundary, direction: -1);
        var engine = new RuleEngine(new[] { rule });
        var tracker = new Tracker(graceFrames: 10);
        var centers = new[] { 0.80, 0.70, 0.60, 0.50, 0.40, 0.30 };
        for (var i = 0; i < centers.Length; i++)
        {
            var at = T0.AddSeconds(i);
            var frame = tracker.Update(i, new[] { new Detection(CenterBox(centers[i]), 0.9, Detection.PersonClassId) }, at, false);
            var fired = engine.Evaluate(at, frame.ActiveTracks.Select(t => new ScoredTrack(t, 0.9, at)).ToList(), "m1");
            Assert.Empty(fired);
        }
    }

    [Fact]
    public void BoundaryCrossing_SingleFrameBlip_Silent()
    {
        var boundary = new DirectedBoundary(new TrackedPoint(0.6, 0.1), new TrackedPoint(0.6, 0.9));
        var rule = new RuleConfig("b", RuleType.BoundaryCrossing, 1, boundary: boundary, direction: -1);
        var engine = new RuleEngine(new[] { rule });
        var tracker = new Tracker(graceFrames: 10);
        // Only two frames: trail too short for the sustained (2-step) crossing test.
        var starts = new[] { 0.30, 0.40 };
        for (var i = 0; i < starts.Length; i++)
        {
            var at = T0.AddSeconds(i);
            var frame = tracker.Update(i, new[] { new Detection(CenterBox(starts[i]), 0.9, Detection.PersonClassId) }, at, false);
            Assert.Empty(engine.Evaluate(at, frame.ActiveTracks.Select(t => new ScoredTrack(t, 0.9, at)).ToList(), "m1"));
        }
    }

    [Fact]
    public void Boundary_OppositeCrossing_Rearms_AndRefires()
    {
        var boundary = new DirectedBoundary(new TrackedPoint(0.6, 0.1), new TrackedPoint(0.6, 0.9));
        var rule = new RuleConfig("b", RuleType.BoundaryCrossing, 1, boundary: boundary, direction: -1);
        var engine = new RuleEngine(new[] { rule });
        var tracker = new Tracker(graceFrames: 10);
        var fired = new List<RuleEvent>();
        long frame = 0;

        // Forward (+x, sign -1): sustained crossing fires once.
        DriveLeg(tracker, engine, new[] { 0.30, 0.40, 0.50, 0.60, 0.70 }, Times(T0, 5), fired, ref frame);
        Assert.Single(fired);
        Assert.Equal(T0.AddSeconds(3), fired[0].EventTimeUtc);

        // Back across (-x, sign +1): the opposite crossing re-arms, never fires.
        DriveLeg(tracker, engine, new[] { 0.60, 0.50, 0.40, 0.30 }, Times(T0.AddSeconds(5), 4), fired, ref frame);
        Assert.Single(fired);

        // Forward again: refire on the fresh expected-direction crossing.
        DriveLeg(tracker, engine, new[] { 0.40, 0.50, 0.60, 0.70 }, Times(T0.AddSeconds(9), 4), fired, ref frame);
        Assert.Equal(2, fired.Count);
        Assert.Equal(T0.AddSeconds(11), fired[1].EventTimeUtc);
    }

    [Fact]
    public void Boundary_OppositeCrossing_Rearm_StillSubjectToCooldown()
    {
        var boundary = new DirectedBoundary(new TrackedPoint(0.6, 0.1), new TrackedPoint(0.6, 0.9));
        var rule = new RuleConfig(
            "b", RuleType.BoundaryCrossing, 1, cooldownSeconds: 60.0, boundary: boundary, direction: -1);
        var engine = new RuleEngine(new[] { rule });
        var tracker = new Tracker(graceFrames: 10);
        var fired = new List<RuleEvent>();
        long frame = 0;

        DriveLeg(tracker, engine, new[] { 0.30, 0.40, 0.50, 0.60, 0.70 }, Times(T0, 5), fired, ref frame);
        Assert.Single(fired); // fires at t=3

        // Opposite crossing re-arms the latch...
        DriveLeg(tracker, engine, new[] { 0.60, 0.50, 0.40, 0.30 }, Times(T0.AddSeconds(5), 4), fired, ref frame);
        Assert.Single(fired);

        // ...but the fresh expected crossing at t=11 is inside the 60s cooldown: silent.
        DriveLeg(tracker, engine, new[] { 0.40, 0.50, 0.60, 0.70 }, Times(T0.AddSeconds(9), 4), fired, ref frame);
        Assert.Single(fired);

        // Cross back (re-arm again, still silent), then forward after the cooldown: fires.
        DriveLeg(tracker, engine, new[] { 0.60, 0.50, 0.40, 0.30 }, Times(T0.AddSeconds(13), 4), fired, ref frame);
        Assert.Single(fired);
        DriveLeg(tracker, engine, new[] { 0.40, 0.50, 0.60, 0.70 }, Times(T0.AddSeconds(63), 4), fired, ref frame);
        Assert.Equal(2, fired.Count);
        Assert.Equal(T0.AddSeconds(65), fired[1].EventTimeUtc);
    }

    [Fact]
    public void TrackerHandoff_GraceHeldTrack_KeepsDwellAlive_ThenRetireResets()
    {
        // Presence follows the TRACKER: scored straight from ActiveTracks (Task 5 pattern).
        var rule = new RuleConfig(
            "c", RuleType.Dwell, 1, dwellSeconds: 5.0, polygon: Square(), scheduleId: null);
        var engine = new RuleEngine(new[] { rule });
        var tracker = new Tracker(graceFrames: 10);
        var box = new BoundingBox(0.4, 0.4, 0.2, 0.2);
        var fired = new List<RuleEvent>();

        // Detections on t=0..2, then an occlusion gap: the tracker holds the stale box.
        for (var s = 0; s <= 6; s++)
        {
            var dets = s <= 2
                ? new[] { new Detection(box, 0.9, Detection.PersonClassId) }
                : Array.Empty<Detection>();
            var frame = tracker.Update(s, dets, T0.AddSeconds(s), false);
            if (s >= 3)
            {
                Assert.Single(frame.ActiveTracks); // still held inside grace
            }

            fired.AddRange(engine.Evaluate(T0.AddSeconds(s), ScoreFromTracks(frame), "m1"));
        }

        // Dwell (5s from t=0) fires at t=5 on the grace-held stale box — no live
        // detection since t=2.
        var first = Assert.Single(fired);
        Assert.Equal(T0.AddSeconds(5), first.EventTimeUtc);
        var firstId = first.TrackId;

        // The gap reaches grace (misses 5..10 over t=7..12): the track retires, the episode drops.
        TrackedFrame last = null!;
        for (var s = 7; s <= 12; s++)
        {
            last = tracker.Update(s, Array.Empty<Detection>(), T0.AddSeconds(s), false);
            Assert.Empty(engine.Evaluate(T0.AddSeconds(s), ScoreFromTracks(last), "m1"));
        }

        Assert.Empty(last.ActiveTracks);

        // Re-entry is a NEW track id and restarts dwell from scratch.
        var reentry = tracker.Update(
            13, new[] { new Detection(box, 0.9, Detection.PersonClassId) }, T0.AddSeconds(13), false);
        var reentryId = Assert.Single(reentry.ActiveTracks).Id;
        Assert.NotEqual(firstId, reentryId);
        Assert.Empty(engine.Evaluate(T0.AddSeconds(13), ScoreFromTracks(reentry), "m1"));
        for (var s = 14; s <= 17; s++)
        {
            var frame = tracker.Update(
                s, new[] { new Detection(box, 0.9, Detection.PersonClassId) }, T0.AddSeconds(s), false);
            Assert.Empty(engine.Evaluate(T0.AddSeconds(s), ScoreFromTracks(frame), "m1"));
        }

        var refireFrame = tracker.Update(
            18, new[] { new Detection(box, 0.9, Detection.PersonClassId) }, T0.AddSeconds(18), false);
        var refire = engine.Evaluate(T0.AddSeconds(18), ScoreFromTracks(refireFrame), "m1");
        var e = Assert.Single(refire);
        Assert.Equal(reentryId, e.TrackId);
    }

    [Fact]
    public void OpenSchedule_FreezesZoneEpisode_NoFireOnFirstClosedFrame()
    {
        var engine = new RuleEngine(new[] { RuleA() }, schedulesById: Schedules());
        // 10:00 EST school hours -> open.
        var open = T0.AddHours(12);
        for (var i = 0; i < 5; i++)
        {
            // Inside frames while open: frozen, never counted — even 5 in a row stay silent.
            Assert.Empty(Eval(engine, open.AddSeconds(i), "m1", Score(InsideTrack(1, i), open.AddSeconds(i))));
        }

        // 23:00 EST, schedule closed again: persistence restarts at close — the open
        // frames bought nothing, so the first closed frame cannot fire.
        var closed = new DateTimeOffset(2026, 1, 16, 4, 0, 0, TimeSpan.Zero);
        Assert.Empty(Eval(engine, closed, "m1", Score(InsideTrack(1, 10), closed)));
        Assert.Empty(Eval(engine, closed.AddSeconds(1), "m1", Score(InsideTrack(1, 11), closed.AddSeconds(1))));
        Assert.Single(Eval(engine, closed.AddSeconds(2), "m1", Score(InsideTrack(1, 12), closed.AddSeconds(2))));
    }

    [Fact]
    public void OpenSchedule_FreezesDwellClock()
    {
        var rule = new RuleConfig(
            "c", RuleType.Dwell, 1, dwellSeconds: 10.0, polygon: Square(), scheduleId: "night");
        var engine = new RuleEngine(new[] { rule }, schedulesById: Schedules());
        var open = T0.AddHours(12);
        for (var i = 0; i <= 30; i++)
        {
            // 30s of presence while open: frozen — PresenceStart is never even set.
            Assert.Empty(Eval(engine, open.AddSeconds(i), "m1", Score(InsideTrack(1, i), open.AddSeconds(i))));
        }

        var closed = new DateTimeOffset(2026, 1, 16, 4, 0, 0, TimeSpan.Zero);
        Assert.Empty(Eval(engine, closed, "m1", Score(InsideTrack(1, 100), closed)));
        Assert.Empty(Eval(engine, closed.AddSeconds(9), "m1", Score(InsideTrack(1, 101), closed.AddSeconds(9))));
        Assert.Single(Eval(engine, closed.AddSeconds(10), "m1", Score(InsideTrack(1, 102), closed.AddSeconds(10))));
    }

    [Fact]
    public void Dwell_FiresAfterDwellSeconds_OnEventTimestamps()
    {
        var rule = new RuleConfig(
            "c", RuleType.Dwell, 1, dwellSeconds: 5.0, polygon: Square(), scheduleId: "night");
        var engine = new RuleEngine(new[] { rule }, schedulesById: Schedules());
        Assert.Empty(Eval(engine, T0, "m1", Score(InsideTrack(1, 0), T0)));
        Assert.Empty(Eval(engine, T0.AddSeconds(4.9), "m1", Score(InsideTrack(1, 1), T0.AddSeconds(4.9))));
        var fired = Eval(engine, T0.AddSeconds(5.0), "m1", Score(InsideTrack(1, 2), T0.AddSeconds(5.0)));
        Assert.Single(fired);
    }

    [Fact]
    public void Dwell_ExitRestartsClock()
    {
        var rule = new RuleConfig(
            "c", RuleType.Dwell, 1, dwellSeconds: 5.0, polygon: Square(), scheduleId: "night");
        var engine = new RuleEngine(new[] { rule }, schedulesById: Schedules());
        Eval(engine, T0, "m1", Score(InsideTrack(1, 0), T0));
        Eval(engine, T0.AddSeconds(4), "m1", Score(InsideTrack(1, 1), T0.AddSeconds(4)));
        // Exit before the dwell elapses: clock restarts.
        Eval(engine, T0.AddSeconds(5), "m1", Score(OutsideTrack(1, 2), T0.AddSeconds(5)));
        Assert.Empty(Eval(engine, T0.AddSeconds(10), "m1", Score(InsideTrack(1, 3), T0.AddSeconds(10))));
        var fired = Eval(engine, T0.AddSeconds(15), "m1", Score(InsideTrack(1, 4), T0.AddSeconds(15)));
        Assert.Single(fired);
    }

    [Fact]
    public void PoolZone_UnauthorisedEntry_Fires()
    {
        var rule = new RuleConfig(
            "d", RuleType.PoolZone, 1, persistenceFrames: 2, polygon: Square(),
            scheduleId: "night", poolId: "pool-a");
        var engine = new RuleEngine(new[] { rule }, schedulesById: Schedules());
        Eval(engine, T0, "m1", Score(InsideTrack(1, 0), T0));
        var fired = Eval(engine, T0.AddSeconds(1), "m1", Score(InsideTrack(1, 1), T0.AddSeconds(1)));
        Assert.Single(fired);
    }

    [Fact]
    public void PoolZone_AuthorisedSession_Silent()
    {
        var schedule = new Schedule(
            "America/New_York",
            new[] { new DailyWindow(new TimeOnly(22, 0), new TimeOnly(6, 0)) },
            poolSessions: new[]
            {
                new PoolSession("pool-a", T0.AddHours(-1), T0.AddHours(1)),
            });
        var maps = new Dictionary<string, Schedule>(StringComparer.Ordinal) { ["night"] = schedule };
        var rule = new RuleConfig(
            "d", RuleType.PoolZone, 1, persistenceFrames: 2, polygon: Square(),
            scheduleId: "night", poolId: "pool-a");
        var engine = new RuleEngine(new[] { rule }, schedulesById: maps);
        for (var i = 0; i < 4; i++)
        {
            Assert.Empty(Eval(engine, T0.AddSeconds(i), "m1", Score(InsideTrack(1, i), T0.AddSeconds(i))));
        }
    }

    [Fact]
    public void Cooldown_SuppressesRefire_ThenRearms()
    {
        var rule = new RuleConfig(
            "a", RuleType.ZoneIntrusion, 1, persistenceFrames: 2, cooldownSeconds: 60.0,
            polygon: Square(), scheduleId: "night");
        var engine = new RuleEngine(new[] { rule }, schedulesById: Schedules());
        Eval(engine, T0, "m1", Score(InsideTrack(1, 0), T0));
        Assert.Single(Eval(engine, T0.AddSeconds(1), "m1", Score(InsideTrack(1, 1), T0.AddSeconds(1))));

        // New episode inside the cooldown: silent.
        Eval(engine, T0.AddSeconds(2), "m1", Score(OutsideTrack(1, 2), T0.AddSeconds(2)));
        Eval(engine, T0.AddSeconds(30), "m1", Score(InsideTrack(1, 3), T0.AddSeconds(30)));
        Assert.Empty(Eval(engine, T0.AddSeconds(31), "m1", Score(InsideTrack(1, 4), T0.AddSeconds(31))));

        // New episode after the cooldown: fires again.
        Eval(engine, T0.AddSeconds(32), "m1", Score(OutsideTrack(1, 5), T0.AddSeconds(32)));
        Eval(engine, T0.AddSeconds(120), "m1", Score(InsideTrack(1, 6), T0.AddSeconds(120)));
        Assert.Single(Eval(engine, T0.AddSeconds(121), "m1", Score(InsideTrack(1, 7), T0.AddSeconds(121))));
    }

    [Fact]
    public void Pause_Suppresses_ThenExpiryRearms()
    {
        var rule = new RuleConfig(
            "a", RuleType.ZoneIntrusion, 1, persistenceFrames: 2, polygon: Square(),
            scheduleId: "night", pausedUntilUtc: T0.AddSeconds(100));
        var engine = new RuleEngine(new[] { rule }, schedulesById: Schedules());
        Assert.Empty(Eval(engine, T0, "m1", Score(InsideTrack(1, 0), T0)));
        Assert.Empty(Eval(engine, T0.AddSeconds(1), "m1", Score(InsideTrack(1, 1), T0.AddSeconds(1))));

        // Counting restarts at expiry: two fresh frames fire.
        Assert.Empty(Eval(engine, T0.AddSeconds(100), "m1", Score(InsideTrack(1, 2), T0.AddSeconds(100))));
        Assert.Single(Eval(engine, T0.AddSeconds(101), "m1", Score(InsideTrack(1, 3), T0.AddSeconds(101))));
    }

    [Fact]
    public void VersionChange_DiscardsPartialState()
    {
        var engine = new RuleEngine(new[] { RuleA(version: 1) }, schedulesById: Schedules());
        Eval(engine, T0, "m1", Score(InsideTrack(1, 0), T0));
        Eval(engine, T0.AddSeconds(1), "m1", Score(InsideTrack(1, 1), T0.AddSeconds(1)));

        engine.UpdateRules(new[] { RuleA(version: 2) });

        // Two accumulated frames are gone: needs three fresh frames under v2.
        Assert.Empty(Eval(engine, T0.AddSeconds(2), "m1", Score(InsideTrack(1, 2), T0.AddSeconds(2))));
        Assert.Empty(Eval(engine, T0.AddSeconds(3), "m1", Score(InsideTrack(1, 3), T0.AddSeconds(3))));
        var fired = Eval(engine, T0.AddSeconds(4), "m1", Score(InsideTrack(1, 4), T0.AddSeconds(4)));
        var e = Assert.Single(fired);
        Assert.Equal(2, e.RuleVersion);
    }

    [Fact]
    public void UpdateRules_SameVersion_KeepsPartialState()
    {
        var engine = new RuleEngine(new[] { RuleA(version: 1) }, schedulesById: Schedules());
        Eval(engine, T0, "m1", Score(InsideTrack(1, 0), T0));
        Eval(engine, T0.AddSeconds(1), "m1", Score(InsideTrack(1, 1), T0.AddSeconds(1)));

        // Same id AND same version: the in-progress InsideCount survives the config pull.
        engine.UpdateRules(new[] { RuleA(version: 1) });

        var fired = Eval(engine, T0.AddSeconds(2), "m1", Score(InsideTrack(1, 2), T0.AddSeconds(2)));
        var e = Assert.Single(fired);
        Assert.Equal(1, e.RuleVersion);
    }

    [Fact]
    public void UpdateRules_RemoveAndReadd_StaleCooldownSuppresses()
    {
        var rule = new RuleConfig(
            "a", RuleType.ZoneIntrusion, 1, persistenceFrames: 2, cooldownSeconds: 60.0,
            polygon: Square(), scheduleId: null);
        var engine = new RuleEngine(new[] { rule });
        Eval(engine, T0, "m1", Score(InsideTrack(1, 0), T0));
        Assert.Single(Eval(engine, T0.AddSeconds(1), "m1", Score(InsideTrack(1, 1), T0.AddSeconds(1))));
        Eval(engine, T0.AddSeconds(2), "m1", Score(OutsideTrack(1, 2), T0.AddSeconds(2)));

        engine.UpdateRules(Array.Empty<RuleConfig>()); // removed: episode discarded
        engine.UpdateRules(new[] { rule }); // re-added, same version

        // Fresh episode, but the stale cooldown marker (from t=1) survives: silent.
        Assert.Empty(Eval(engine, T0.AddSeconds(30), "m1", Score(InsideTrack(1, 3), T0.AddSeconds(30))));
        Assert.Empty(Eval(engine, T0.AddSeconds(31), "m1", Score(InsideTrack(1, 4), T0.AddSeconds(31))));

        // After the cooldown: fires again.
        Eval(engine, T0.AddSeconds(32), "m1", Score(OutsideTrack(1, 5), T0.AddSeconds(32)));
        Eval(engine, T0.AddSeconds(120), "m1", Score(InsideTrack(1, 6), T0.AddSeconds(120)));
        Assert.Single(Eval(engine, T0.AddSeconds(121), "m1", Score(InsideTrack(1, 7), T0.AddSeconds(121))));
    }

    [Fact]
    public void MultiRule_SameFrameFireOrder_SortedByRuleId()
    {
        // Inserted zebra-first: fired order must still be alpha, zebra (sorted by rule id).
        var zebra = new RuleConfig(
            "zebra", RuleType.ZoneIntrusion, 1, persistenceFrames: 1, polygon: Square(), scheduleId: null);
        var alpha = new RuleConfig(
            "alpha", RuleType.ZoneIntrusion, 1, persistenceFrames: 1, polygon: Square(), scheduleId: null);
        var engine = new RuleEngine(new[] { zebra, alpha });

        var fired = Eval(engine, T0, "m1", Score(InsideTrack(1, 0), T0));

        Assert.Equal(2, fired.Count);
        Assert.Equal("alpha", fired[0].RuleId);
        Assert.Equal("zebra", fired[1].RuleId);
    }

    [Fact]
    public void ConfidenceGate_BelowThresholdFramesBreakConsecutiveness()
    {
        var engine = new RuleEngine(new[] { RuleA() }, schedulesById: Schedules());
        Eval(engine, T0, "m1", Score(InsideTrack(1, 0), T0, confidence: 0.9));
        Eval(engine, T0.AddSeconds(1), "m1", Score(InsideTrack(1, 1), T0.AddSeconds(1), confidence: 0.9));
        // Below-threshold frame: skipped as if absent (detector filters these in
        // production; the engine drops the episode defensively).
        Assert.Empty(Eval(engine, T0.AddSeconds(2), "m1", Score(InsideTrack(1, 2), T0.AddSeconds(2), confidence: 0.1)));
        Assert.Empty(Eval(engine, T0.AddSeconds(3), "m1", Score(InsideTrack(1, 3), T0.AddSeconds(3), confidence: 0.9)));
        Assert.Empty(Eval(engine, T0.AddSeconds(4), "m1", Score(InsideTrack(1, 4), T0.AddSeconds(4), confidence: 0.9)));
        Assert.Single(Eval(engine, T0.AddSeconds(5), "m1", Score(InsideTrack(1, 5), T0.AddSeconds(5), confidence: 0.9)));
    }

    [Fact]
    public void HealthyFrameAge_TracksEventTime_NotWallClock()
    {
        var engine = new RuleEngine(Array.Empty<RuleConfig>());
        Assert.Null(engine.HealthyFrameAgeAt(T0));
        engine.NoteHealthyFrame(T0);
        Assert.Equal(TimeSpan.FromSeconds(30), engine.HealthyFrameAgeAt(T0.AddSeconds(30)));
    }

    [Fact]
    public void RuleConfig_InvalidCombinations_Throw()
    {
        // Type A with a boundary (instead of a polygon) is rejected.
        Assert.Throws<ArgumentException>(() => new RuleConfig(
            "x", RuleType.ZoneIntrusion, 1, polygon: null,
            boundary: new DirectedBoundary(new TrackedPoint(0, 0), new TrackedPoint(1, 1))));
        // Type B without a direction is rejected.
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuleConfig(
            "x", RuleType.BoundaryCrossing, 1,
            boundary: new DirectedBoundary(new TrackedPoint(0, 0), new TrackedPoint(1, 1))));
    }
}
