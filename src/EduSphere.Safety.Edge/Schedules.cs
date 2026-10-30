namespace EduSphere.Safety.Edge;

/// <summary>
/// School IANA-timezone schedule: daily closed windows plus date overrides and pool
/// sessions. All evaluation uses caller-supplied event timestamps — this file never
/// touches the wall clock (no <c>DateTime.Now</c>/<c>DateTime.UtcNow</c>).
/// Semantics: a rule fires only while its schedule says CLOSED (after-hours arming).
/// A null <c>ScheduleId</c> on a rule means "no schedule gate": always armed.
/// </summary>
public sealed class Schedule
{
    /// <summary>Creates a schedule. Unknown timezone ids throw at first evaluation.</summary>
    public Schedule(
        string ianaTimezone,
        IReadOnlyList<DailyWindow> closedWindows,
        IReadOnlyList<DateOnly>? holidays = null,
        IReadOnlyDictionary<DateOnly, bool>? exceptionClosedByDate = null,
        IReadOnlyList<PoolSession>? poolSessions = null)
    {
        if (string.IsNullOrWhiteSpace(ianaTimezone))
        {
            throw new ArgumentException("IANA timezone id must not be empty.", nameof(ianaTimezone));
        }

        IanaTimezone = ianaTimezone;
        ClosedWindows = closedWindows ?? throw new ArgumentNullException(nameof(closedWindows));
        Holidays = holidays;
        ExceptionClosedByDate = exceptionClosedByDate;
        PoolSessions = poolSessions;
    }

    /// <summary>IANA id resolved via <see cref="TimeZoneInfo.FindSystemTimeZoneById"/>.</summary>
    public string IanaTimezone { get; }

    /// <summary>
    /// Daily closed windows in LOCAL wall time. Empty means never closed (always open).
    /// </summary>
    public IReadOnlyList<DailyWindow> ClosedWindows { get; }

    /// <summary>
    /// Holiday dates (local calendar days): the school is shut, so the whole day counts
    /// as CLOSED — regular open windows are suppressed and after-hours rules stay armed.
    /// </summary>
    public IReadOnlyList<DateOnly>? Holidays { get; }

    /// <summary>
    /// Per-date all-day overrides: true = forced closed, false = forced open.
    /// Wins over holidays and daily windows.
    /// </summary>
    public IReadOnlyDictionary<DateOnly, bool>? ExceptionClosedByDate { get; }

    /// <summary>Authorised pool-use sessions; a type-D entry inside one stays silent.</summary>
    public IReadOnlyList<PoolSession>? PoolSessions { get; }
}

/// <summary>
/// One daily closed window in local wall time, e.g. 22:00–06:00. Overnight-aware: when
/// <see cref="End"/> is earlier than (or equal to) <see cref="Start"/>, the window wraps
/// past midnight. Start is inclusive, end is exclusive. Start == End covers the whole day.
/// </summary>
public sealed record DailyWindow(TimeOnly Start, TimeOnly End)
{
    /// <summary>True when local wall time <paramref name="t"/> falls in this window.</summary>
    public bool Contains(TimeOnly t) =>
        End > Start ? t >= Start && t < End : t >= Start || t < End;
}

/// <summary>
/// One authorised pool-use session. Instants are UTC event-time bounds, inclusive on
/// both ends. Manual calendar entries keyed by pool id.
/// </summary>
public sealed class PoolSession
{
    /// <summary>Creates a session; start must not be after end.</summary>
    public PoolSession(string poolId, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        if (string.IsNullOrWhiteSpace(poolId))
        {
            throw new ArgumentException("Pool id must not be empty.", nameof(poolId));
        }

        if (startUtc > endUtc)
        {
            throw new ArgumentException("Session start must not be after session end.", nameof(startUtc));
        }

        PoolId = poolId;
        StartUtc = startUtc;
        EndUtc = endUtc;
    }

    /// <summary>Pool the session authorises.</summary>
    public string PoolId { get; }

    /// <summary>Session start (UTC, inclusive).</summary>
    public DateTimeOffset StartUtc { get; }

    /// <summary>Session end (UTC, inclusive).</summary>
    public DateTimeOffset EndUtc { get; }

    /// <summary>Authorised while <c>StartUtc &lt;= eventTime &lt;= EndUtc</c>.</summary>
    public bool Covers(DateTimeOffset eventTimeUtc) => StartUtc <= eventTimeUtc && eventTimeUtc <= EndUtc;
}

/// <summary>
/// Pure schedule evaluation over event timestamps. See <see cref="IsClosedAt"/> for the
/// pinned DST policy. IANA database note: zone lookup goes through
/// <see cref="TimeZoneInfo.FindSystemTimeZoneById"/>, which needs the IANA timezone
/// database — present on Linux/macOS CI; on Windows it resolves only with ICU/ICU data
/// providing IANA ids, so schedule tests are Linux/macOS-CI-pinned.
/// </summary>
public sealed class ScheduleEvaluator
{
    /// <summary>
    /// True when <paramref name="eventTimeUtc"/> falls in a closed period of
    /// <paramref name="schedule"/>. Pure: the timestamp is passed in, never read here.
    /// DST policy (pinned by tests):
    /// <list type="bullet">
    /// <item>Spring-forward gap: a nonexistent local wall time can never be produced by
    /// mapping a real UTC instant — <see cref="TimeZoneInfo"/> maps gap-adjacent instants
    /// to the later (daylight) offset, and closure follows that mapped wall time. A
    /// window lying entirely inside the gap is therefore never closed.</item>
    /// <item>Fall-back ambiguity: a local wall time occurring twice is evaluated ONCE —
    /// both occurrences share the same local wall time and daily windows are wall-time
    /// checks, so one evaluation covers both. (An earlier revision looped over
    /// <c>GetAmbiguousTimeOffsets</c>, but every iteration computed the identical
    /// wall-time result; the loop was simplified to this single check. If a
    /// UTC-anchored check is ever layered onto this path, it MUST evaluate both
    /// occurrences and count the instant as closed if either says closed.)</item>
    /// </list>
    /// </summary>
    public bool IsClosedAt(DateTimeOffset eventTimeUtc, Schedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.IanaTimezone);
        var local = TimeZoneInfo.ConvertTime(eventTimeUtc, zone);
        var date = DateOnly.FromDateTime(local.DateTime);
        var time = TimeOnly.FromDateTime(local.DateTime);

        if (schedule.ExceptionClosedByDate is not null &&
            schedule.ExceptionClosedByDate.TryGetValue(date, out var exceptionClosed))
        {
            return exceptionClosed;
        }

        if (schedule.Holidays is not null && schedule.Holidays.Contains(date))
        {
            return true;
        }

        return WindowsContain(schedule, time);
    }

    private static bool WindowsContain(Schedule schedule, TimeOnly time) =>
        schedule.ClosedWindows.Any(window => window.Contains(time));
}
