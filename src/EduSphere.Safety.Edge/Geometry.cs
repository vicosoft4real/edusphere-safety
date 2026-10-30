namespace EduSphere.Safety.Edge;

/// <summary>
/// A zone polygon in normalized frame coordinates (0..1 on each axis, same basis as
/// <see cref="BoundingBox"/>). Pure: no clock, no IO.
/// Which point decides: the detection/track foot point (bottom-center), never the centroid.
/// Resolution/aspect safety: because every coordinate is normalized, a stream resolution
/// change cannot shift zones; a field-of-view change instead invalidates track identity
/// (see the <c>IsResolutionChange</c> track-reset contract on <see cref="Frame"/>).
/// </summary>
public sealed class PolygonZone
{
    /// <summary>
    /// Absolute tolerance for on-edge classification, matching the <see cref="BoundingBox"/>
    /// 1e-9 slack: foot points may sit marginally outside 0..1 from rounding, and zone
    /// checks must tolerate that instead of throwing. Inputs are never range-validated
    /// here — any finite point is classifiable.
    /// </summary>
    private const double Tolerance = 1e-9;

    private readonly TrackedPoint[] _vertices;

    /// <summary>Creates a zone from at least 3 normalized vertices (any winding).</summary>
    public PolygonZone(IEnumerable<TrackedPoint> vertices)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        _vertices = vertices.ToArray();
        if (_vertices.Any(static v => v is null))
        {
            throw new ArgumentException("Zone vertices must not contain null.", nameof(vertices));
        }

        if (_vertices.Length < 3)
        {
            throw new ArgumentException("A polygon zone needs at least 3 vertices.", nameof(vertices));
        }
    }

    /// <summary>Zone vertices in construction order.</summary>
    public IReadOnlyList<TrackedPoint> Vertices => Array.AsReadOnly(_vertices);

    /// <summary>
    /// Ray-casting containment. Boundary counts as INSIDE: this is the entry side of the
    /// hysteresis pair — the exit side applies its own outer band via
    /// <see cref="ContainsWithMargin(TrackedPoint, double)"/>, so call sites stay explicit
    /// about which side they are checking.
    /// </summary>
    public bool Contains(TrackedPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);

        var n = _vertices.Length;
        for (var i = 0; i < n; i++)
        {
            if (IsOnSegment(_vertices[i], _vertices[(i + 1) % n], point))
            {
                return true;
            }
        }

        var inside = false;
        for (var i = 0; i < n; i++)
        {
            var a = _vertices[i];
            var b = _vertices[(i + 1) % n];
            if ((a.Y > point.Y) != (b.Y > point.Y))
            {
                var xCross = a.X + ((point.Y - a.Y) / (b.Y - a.Y)) * (b.X - a.X);
                if (point.X < xCross)
                {
                    inside = !inside;
                }
            }
        }

        return inside;
    }

    /// <summary>
    /// <see cref="Contains"/> OR within <paramref name="margin"/> (normalized units) of any
    /// edge. The exit side of the hysteresis pair: a track counts as exited only when it
    /// leaves this outer band, so boundary jitter cannot flap entry/exit. Margin 0 is
    /// exactly <see cref="Contains"/>.
    /// </summary>
    public bool ContainsWithMargin(TrackedPoint point, double margin)
    {
        ArgumentNullException.ThrowIfNull(point);
        if (double.IsNaN(margin) || double.IsInfinity(margin) || margin < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(margin), "Margin must be a finite value >= 0.");
        }

        if (Contains(point))
        {
            return true;
        }

        if (margin == 0.0)
        {
            return false;
        }

        var n = _vertices.Length;
        for (var i = 0; i < n; i++)
        {
            if (DistanceToSegment(point, _vertices[i], _vertices[(i + 1) % n]) <= margin + Tolerance)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsOnSegment(TrackedPoint a, TrackedPoint b, TrackedPoint p)
    {
        var cross = ((b.X - a.X) * (p.Y - a.Y)) - ((b.Y - a.Y) * (p.X - a.X));
        if (Math.Abs(cross) > Tolerance)
        {
            return false;
        }

        var dot = ((p.X - a.X) * (b.X - a.X)) + ((p.Y - a.Y) * (b.Y - a.Y));
        if (dot < -Tolerance)
        {
            return false;
        }

        var lenSq = ((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y));
        return dot <= lenSq + Tolerance;
    }

    private static double DistanceToSegment(TrackedPoint p, TrackedPoint a, TrackedPoint b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lenSq = (dx * dx) + (dy * dy);
        if (lenSq == 0.0)
        {
            return Math.Sqrt(((p.X - a.X) * (p.X - a.X)) + ((p.Y - a.Y) * (p.Y - a.Y)));
        }

        var t = (((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / lenSq;
        t = Math.Clamp(t, 0.0, 1.0);
        var cx = a.X + (t * dx);
        var cy = a.Y + (t * dy);
        return Math.Sqrt(((p.X - cx) * (p.X - cx)) + ((p.Y - cy) * (p.Y - cy)));
    }
}

/// <summary>
/// A directed line segment A→B in normalized coordinates. A crossing is reported only
/// when the foot-point path both intersects the segment AND moves consistently across it
/// over two consecutive frame steps, so single-frame jitter never fires.
/// Sign convention: +1 means the two-step path moves toward the left of the A→B
/// direction (cross product of boundary direction with movement is positive), -1 toward
/// the right. Rule B matches this sign against its expected direction; the opposite
/// crossing stays silent.
/// </summary>
public sealed class DirectedBoundary
{
    private const double Tolerance = 1e-12;

    /// <summary>Creates a non-degenerate directed segment.</summary>
    public DirectedBoundary(TrackedPoint a, TrackedPoint b)
    {
        A = a ?? throw new ArgumentNullException(nameof(a));
        B = b ?? throw new ArgumentNullException(nameof(b));
        var dx = B.X - A.X;
        var dy = B.Y - A.Y;
        if ((dx * dx) + (dy * dy) <= Tolerance * Tolerance)
        {
            throw new ArgumentException("Boundary endpoints must be distinct.", nameof(b));
        }
    }

    /// <summary>Segment start.</summary>
    public TrackedPoint A { get; }

    /// <summary>Segment end; the A→B direction is the crossing reference.</summary>
    public TrackedPoint B { get; }

    /// <summary>
    /// Which side of the directed line <paramref name="point"/> lies on: +1 left of A→B,
    /// -1 right, 0 on the line (within tolerance). Line side only, not segment bounds.
    /// </summary>
    public int SideOf(TrackedPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        var cross = ((B.X - A.X) * (point.Y - A.Y)) - ((B.Y - A.Y) * (point.X - A.X));
        return Sign(cross);
    }

    /// <summary>
    /// Sustained-crossing test over a foot-point trail (oldest first). Returns +1/-1 when
    /// BOTH of the last two consecutive frame steps move to the same nonzero side AND the
    /// two-frame path (oldest→newest of the last three points) intersects the A→B
    /// segment; otherwise 0 (fewer than 3 points, a stationary/parallel step, disagreeing
    /// steps, or a path that misses the segment — e.g. parallel motion nearby, or a line
    /// crossing outside the segment bounds).
    /// </summary>
    public int CrossedSide(IReadOnlyList<TrackedPoint> footTrail)
    {
        ArgumentNullException.ThrowIfNull(footTrail);
        if (footTrail.Count < 3)
        {
            return 0;
        }

        var p0 = footTrail[footTrail.Count - 3];
        var p1 = footTrail[footTrail.Count - 2];
        var p2 = footTrail[footTrail.Count - 1];
        ArgumentNullException.ThrowIfNull(p0);
        ArgumentNullException.ThrowIfNull(p1);
        ArgumentNullException.ThrowIfNull(p2);

        var dx = B.X - A.X;
        var dy = B.Y - A.Y;
        var s1 = Sign((dx * (p1.Y - p0.Y)) - (dy * (p1.X - p0.X)));
        var s2 = Sign((dx * (p2.Y - p1.Y)) - (dy * (p2.X - p1.X)));
        if (s1 == 0 || s2 == 0 || s1 != s2)
        {
            return 0;
        }

        return SegmentsIntersect(p0, p2, A, B) ? s1 : 0;
    }

    private static int Sign(double value)
    {
        if (Math.Abs(value) <= Tolerance)
        {
            return 0;
        }

        return value > 0.0 ? 1 : -1;
    }

    private static double Orient(TrackedPoint p, TrackedPoint q, TrackedPoint r) =>
        ((q.X - p.X) * (r.Y - p.Y)) - ((q.Y - p.Y) * (r.X - p.X));

    private static bool Straddle(double o1, double o2) => Sign(o1) * Sign(o2) <= 0;

    private static bool SegmentsIntersect(TrackedPoint p1, TrackedPoint p2, TrackedPoint q1, TrackedPoint q2) =>
        Straddle(Orient(p1, p2, q1), Orient(p1, p2, q2)) &&
        Straddle(Orient(q1, q2, p1), Orient(q1, q2, p2));
}
