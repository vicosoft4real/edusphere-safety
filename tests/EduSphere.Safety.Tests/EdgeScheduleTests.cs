using EduSphere.Safety.Edge;
using Xunit;

// NOTE: every test in this file resolves an IANA timezone id ("America/New_York") via
// TimeZoneInfo, which needs the IANA timezone database — present on Linux/macOS CI.
// On Windows these ids resolve only with ICU data providing IANA names, so this file
// is Linux/macOS-CI-pinned (same note lives on ScheduleEvaluator).
public sealed class EdgeScheduleTests
{
    private const string Ny = "America/New_York";

    private static Schedule Nightly() => new(
        Ny,
        new[] { new DailyWindow(new TimeOnly(22, 0), new TimeOnly(6, 0)) });

    private static readonly ScheduleEvaluator Eval = new();

    [Fact]
    public void OvernightWindow_WrapsPastMidnight()
    {
        var s = Nightly();
        // 2026-01-15: EST (UTC-5).
        Assert.True(Eval.IsClosedAt(new DateTimeOffset(2026, 1, 15, 3, 0, 0, TimeSpan.Zero), s)); // 22:00 local
        Assert.True(Eval.IsClosedAt(new DateTimeOffset(2026, 1, 15, 7, 0, 0, TimeSpan.Zero), s)); // 02:00 local
        Assert.False(Eval.IsClosedAt(new DateTimeOffset(2026, 1, 15, 15, 0, 0, TimeSpan.Zero), s)); // 10:00 local
    }

    [Fact]
    public void OvernightWindow_Boundaries_StartInclusiveEndExclusive()
    {
        var s = Nightly();
        Assert.True(Eval.IsClosedAt(new DateTimeOffset(2026, 1, 15, 3, 0, 0, TimeSpan.Zero), s)); // 22:00
        Assert.False(Eval.IsClosedAt(new DateTimeOffset(2026, 1, 15, 11, 0, 0, TimeSpan.Zero), s)); // 06:00
    }

    [Fact]
    public void SpringForward_GapInstantsMapToLaterOffset()
    {
        // 2026-03-08 02:00 EST -> 03:00 EDT; verified mapping: 06:30Z -> 01:30 EST,
        // 07:00Z -> 03:00 EDT, 07:30Z -> 03:30 EDT. A window entirely inside the gap
        // (02:00-02:30) is therefore never closed: no real instant maps into it.
        var gap = new Schedule(Ny, new[] { new DailyWindow(new TimeOnly(2, 0), new TimeOnly(2, 30)) });
        Assert.False(Eval.IsClosedAt(new DateTimeOffset(2026, 3, 8, 6, 30, 0, TimeSpan.Zero), gap)); // 01:30 EST
        Assert.False(Eval.IsClosedAt(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), gap)); // 03:00 EDT
        Assert.False(Eval.IsClosedAt(new DateTimeOffset(2026, 3, 8, 7, 30, 0, TimeSpan.Zero), gap)); // 03:30 EDT

        // A window spanning the transition follows the mapped wall time on both sides.
        var span = new Schedule(Ny, new[] { new DailyWindow(new TimeOnly(1, 0), new TimeOnly(4, 0)) });
        Assert.True(Eval.IsClosedAt(new DateTimeOffset(2026, 3, 8, 6, 30, 0, TimeSpan.Zero), span));
        Assert.True(Eval.IsClosedAt(new DateTimeOffset(2026, 3, 8, 7, 30, 0, TimeSpan.Zero), span));
    }

    [Fact]
    public void FallBack_BothOccurrences_EvaluatedClosedIfEitherClosed()
    {
        // 2026-11-01 01:30 occurs twice: 05:30Z (EDT) and 06:30Z (EST).
        var s = Nightly(); // 22:00-06:00 covers 01:30 either way
        Assert.True(Eval.IsClosedAt(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), s));
        Assert.True(Eval.IsClosedAt(new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero), s));

        // Narrow window outside 01:30: both occurrences open.
        var day = new Schedule(Ny, new[] { new DailyWindow(new TimeOnly(9, 0), new TimeOnly(17, 0)) });
        Assert.False(Eval.IsClosedAt(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), day));
        Assert.False(Eval.IsClosedAt(new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero), day));
    }

    [Fact]
    public void FallBack_WindowCoveringAmbiguousHour_ClosedForBoth()
    {
        var s = new Schedule(Ny, new[] { new DailyWindow(new TimeOnly(1, 0), new TimeOnly(2, 0)) });
        Assert.True(Eval.IsClosedAt(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), s));
        Assert.True(Eval.IsClosedAt(new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero), s));
    }

    [Fact]
    public void Holiday_SuppressesOpenHours_WholeDayClosed()
    {
        var s = new Schedule(
            Ny,
            new[] { new DailyWindow(new TimeOnly(22, 0), new TimeOnly(6, 0)) },
            holidays: new[] { new DateOnly(2026, 12, 25) });
        // 10:00 EST on Christmas: normally open, holiday forces closed.
        Assert.True(Eval.IsClosedAt(new DateTimeOffset(2026, 12, 25, 15, 0, 0, TimeSpan.Zero), s));
        // Same wall time on a normal school day: open.
        Assert.False(Eval.IsClosedAt(new DateTimeOffset(2026, 12, 24, 15, 0, 0, TimeSpan.Zero), s));
    }

    [Fact]
    public void Exception_OverridesWindowBothWays()
    {
        var date = new DateOnly(2026, 1, 20);
        var forcedOpen = new Schedule(
            Ny,
            new[] { new DailyWindow(new TimeOnly(22, 0), new TimeOnly(6, 0)) },
            exceptionClosedByDate: new Dictionary<DateOnly, bool> { [date] = false });
        // 08:00 UTC = 03:00 EST on Jan 20 (local date): inside the 22:00-06:00
        // window, so only the exception keeps it open.
        Assert.False(Eval.IsClosedAt(new DateTimeOffset(2026, 1, 20, 8, 0, 0, TimeSpan.Zero), forcedOpen));

        var forcedClosed = new Schedule(
            Ny,
            new[] { new DailyWindow(new TimeOnly(9, 0), new TimeOnly(17, 0)) },
            exceptionClosedByDate: new Dictionary<DateOnly, bool> { [date] = true });
        Assert.True(Eval.IsClosedAt(new DateTimeOffset(2026, 1, 20, 15, 0, 0, TimeSpan.Zero), forcedClosed));
    }

    [Fact]
    public void EmptyWindows_NeverClosed()
    {
        var s = new Schedule(Ny, Array.Empty<DailyWindow>());
        Assert.False(Eval.IsClosedAt(new DateTimeOffset(2026, 1, 15, 3, 0, 0, TimeSpan.Zero), s));
    }

    [Fact]
    public void DailyWindow_StartEqualsEnd_CoversWholeDay()
    {
        // Start == End is the whole-day window (t >= S || t < S is always true).
        var midnight = new Schedule(Ny, new[] { new DailyWindow(new TimeOnly(0, 0), new TimeOnly(0, 0)) });
        Assert.True(Eval.IsClosedAt(new DateTimeOffset(2026, 1, 15, 3, 0, 0, TimeSpan.Zero), midnight)); // 22:00 EST
        Assert.True(Eval.IsClosedAt(new DateTimeOffset(2026, 1, 15, 15, 0, 0, TimeSpan.Zero), midnight)); // 10:00 EST
        Assert.True(Eval.IsClosedAt(new DateTimeOffset(2026, 1, 15, 11, 0, 0, TimeSpan.Zero), midnight)); // 06:00 EST

        var afternoon = new Schedule(Ny, new[] { new DailyWindow(new TimeOnly(13, 0), new TimeOnly(13, 0)) });
        Assert.True(Eval.IsClosedAt(new DateTimeOffset(2026, 1, 15, 3, 0, 0, TimeSpan.Zero), afternoon));
        Assert.True(Eval.IsClosedAt(new DateTimeOffset(2026, 1, 15, 18, 0, 0, TimeSpan.Zero), afternoon)); // 13:00 EST
        Assert.True(Eval.IsClosedAt(new DateTimeOffset(2026, 1, 16, 2, 0, 0, TimeSpan.Zero), afternoon)); // 21:00 EST
    }

    [Fact]
    public void UnknownTimezone_Throws()
    {
        var s = new Schedule("Not/AZone", new[] { new DailyWindow(new TimeOnly(22, 0), new TimeOnly(6, 0)) });
        Assert.Throws<TimeZoneNotFoundException>(
            () => Eval.IsClosedAt(new DateTimeOffset(2026, 1, 15, 3, 0, 0, TimeSpan.Zero), s));
    }

    [Fact]
    public void PoolSession_Covers_IsInclusiveBothEnds()
    {
        var session = new PoolSession(
            "pool-a",
            new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 15, 13, 0, 0, TimeSpan.Zero));
        Assert.True(session.Covers(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero)));
        Assert.True(session.Covers(new DateTimeOffset(2026, 1, 15, 13, 0, 0, TimeSpan.Zero)));
        Assert.False(session.Covers(new DateTimeOffset(2026, 1, 15, 13, 0, 1, TimeSpan.Zero)));
    }

    [Fact]
    public void PoolSession_StartAfterEnd_Throws()
    {
        Assert.Throws<ArgumentException>(() => new PoolSession(
            "pool-a",
            new DateTimeOffset(2026, 1, 15, 13, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero)));
    }
}
