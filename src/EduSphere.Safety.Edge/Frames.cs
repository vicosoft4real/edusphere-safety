namespace EduSphere.Safety.Edge;

/// <summary>
/// A single decoded video frame with its capture (event) timestamp.
/// Rule evaluation must use <see cref="CapturedAtUtc"/> (event time), never wall-clock time.
/// </summary>
/// <param name="CameraId">Stable camera identifier; matches <see cref="IFrameSource.CameraId"/>.</param>
/// <param name="Number">
/// Source-assigned frame sequence number. Must be non-negative here; strictly increasing
/// ordering across a stream is guaranteed by the <see cref="IFrameSource"/> contract, not
/// by this record (a record cannot observe other instances).
/// </param>
/// <param name="CapturedAtUtc">
/// Capture (event) time of the frame. Comparisons of <see cref="DateTimeOffset"/> values
/// are offset-aware (they compare the UTC instant), so mixed offsets still order correctly;
/// producers should still prefer UTC. No assertion enforces this — any offset is accepted.
/// </param>
/// <param name="Width">Frame width in pixels; must be positive.</param>
/// <param name="Height">Frame height in pixels; must be positive.</param>
/// <param name="IsResolutionChange">
/// Resolution-change signal: true on the first frame after the stream geometry changed.
/// Chosen over an event because it flows through the same pipeline with no subscription
/// lifecycle; downstream stages (tracker, Task 3) must reset active tracks on it, since
/// all geometry is normalized 0..1 but a field-of-view change invalidates track identity.
/// </param>
/// <param name="Pixels">
/// Decoded pixel payload in documented <c>Rgb24</c> row-major layout: three bytes per
/// pixel (R, G, B in that order), rows top-to-bottom, no stride padding, so
/// <c>Length == Width * Height * 3</c> whenever a payload is present. An EMPTY payload
/// means "no pixels" (scripted/test frames) — it is accepted as-is. A NON-EMPTY payload
/// whose length differs from <c>Width * Height * 3</c> is rejected, in construction and
/// in <c>with</c>/initializer assignment alike. The buffer is retained by reference
/// (not copied): producers that reuse a scratch buffer must hand over a private copy.
/// </param>
public sealed record Frame(
    string CameraId,
    long Number,
    DateTimeOffset CapturedAtUtc,
    int Width,
    int Height,
    bool IsResolutionChange = false,
    ReadOnlyMemory<byte> Pixels = default)
{
    // Every validated property uses a backing field plus an explicit init setter that
    // re-runs construction validation. A `with` expression or object initializer assigns
    // through the init setter WITHOUT re-running the constructor, so a bare
    // auto-property would let `frame with { Width = 0 }` silently build an invalid frame.
    private readonly string _cameraId = ValidateCameraId(CameraId);
    private readonly long _number = ValidateNumber(Number);
    private readonly int _width = ValidateDimension(Width, nameof(Width), "Frame width must be positive.");
    private readonly int _height = ValidateDimension(Height, nameof(Height), "Frame height must be positive.");
    private readonly ReadOnlyMemory<byte> _pixels = ValidatePixels(Pixels, Width, Height);

    public string CameraId { get => _cameraId; init => _cameraId = ValidateCameraId(value); }

    public long Number { get => _number; init => _number = ValidateNumber(value); }

    public int Width { get => _width; init => _width = ValidateDimension(value, nameof(Width), "Frame width must be positive."); }

    public int Height { get => _height; init => _height = ValidateDimension(value, nameof(Height), "Frame height must be positive."); }

    public ReadOnlyMemory<byte> Pixels { get => _pixels; init => _pixels = ValidatePixels(value, Width, Height); }

    private static string ValidateCameraId(string? cameraId) =>
        string.IsNullOrWhiteSpace(cameraId)
            ? throw new ArgumentException("Camera id must not be empty.", nameof(CameraId))
            : cameraId;

    private static long ValidateNumber(long number) =>
        number < 0
            ? throw new ArgumentOutOfRangeException(nameof(Number), "Frame number must be non-negative.")
            : number;

    private static int ValidateDimension(int dimension, string name, string message) =>
        dimension <= 0
            ? throw new ArgumentOutOfRangeException(name, message)
            : dimension;

    private static ReadOnlyMemory<byte> ValidatePixels(ReadOnlyMemory<byte> pixels, int width, int height)
    {
        if (pixels.IsEmpty)
        {
            return pixels; // No payload: scripted/test frames carry geometry only.
        }

        var expected = (long)width * height * 3;
        return pixels.Length != expected
            ? throw new ArgumentException(
                $"Pixel payload must be Rgb24 row-major (Length == Width * Height * 3 == {expected}).",
                nameof(Pixels))
            : pixels;
    }
}

/// <summary>
/// Normalized person bounding box. All components are fractions of frame width/height
/// in 0..1, and the box must lie fully inside the frame (X + W &lt;= 1, Y + H &lt;= 1).
/// W and H must be strictly positive: a detector never emits a zero-area person box,
/// and a zero-area box would divide by zero in tracker IoU (Task 2).
/// </summary>
public sealed record BoundingBox(double X, double Y, double W, double H)
{
    /// <summary>
    /// Absolute slack applied to every normalized-coordinate boundary check here (component
    /// range and box-edge containment alike, for consistency).
    /// Rationale: 1e-9 of frame width is ~6e-7 px at 640 px — far below any real geometry —
    /// while double rounding from pixel-to-normalized conversion is of that order, so an
    /// exact-boundary box (e.g. X + W == 1 computed through floats) must not be rejected:
    /// 1 + 5e-10 passes, 1 + 1e-8 fails.
    /// NOTE: a slack-passing box may therefore yield a <see cref="Detection.FootPoint"/> Y
    /// marginally above 1; zone checks (Task 3) must tolerate that.
    /// </summary>
    private const double EdgeTolerance = 1e-9;

    // Backing fields + explicit init setters (see Frame): `box with { X = 5 }` must throw
    // exactly as construction does.
    private readonly double _x = ValidateOrigin(X, nameof(X), W, "Box right edge (X + W) must not exceed 1.");
    private readonly double _y = ValidateOrigin(Y, nameof(Y), H, "Box bottom edge (Y + H) must not exceed 1.");
    private readonly double _w = ValidateSpan(X, nameof(X), W, nameof(W), "Box right edge (X + W) must not exceed 1.");
    private readonly double _h = ValidateSpan(Y, nameof(Y), H, nameof(H), "Box bottom edge (Y + H) must not exceed 1.");

    public double X { get => _x; init => _x = ValidateOrigin(value, nameof(X), _w, "Box right edge (X + W) must not exceed 1."); }

    public double Y { get => _y; init => _y = ValidateOrigin(value, nameof(Y), _h, "Box bottom edge (Y + H) must not exceed 1."); }

    public double W { get => _w; init => _w = ValidateSpan(X, nameof(X), value, nameof(W), "Box right edge (X + W) must not exceed 1."); }

    public double H { get => _h; init => _h = ValidateSpan(Y, nameof(Y), value, nameof(H), "Box bottom edge (Y + H) must not exceed 1."); }

    private static double ValidateComponent(double value, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < -EdgeTolerance || value > 1.0 + EdgeTolerance)
        {
            throw new ArgumentOutOfRangeException(name, "Normalized box components must be finite values in 0..1 (within 1e-9 rounding slack).");
        }

        return value;
    }

    private static double ValidateOrigin(double start, string startName, double length, string edgeMessage)
    {
        ValidateComponent(start, startName);
        if (start + length > 1.0 + EdgeTolerance)
        {
            throw new ArgumentOutOfRangeException(startName, edgeMessage);
        }

        return start;
    }

    private static double ValidateSpan(double start, string startName, double length, string name, string edgeMessage)
    {
        // Self-sufficient: validates `start` itself instead of assuming X/Y was already
        // validated, so correctness never depends on member declaration order.
        ValidateComponent(start, startName);
        ValidateComponent(length, name);
        if (length <= 0.0)
        {
            throw new ArgumentOutOfRangeException(name, "Box width/height must be positive; zero-area boxes are rejected (tracker IoU would divide by zero).");
        }

        if (start + length > 1.0 + EdgeTolerance)
        {
            throw new ArgumentOutOfRangeException(name, edgeMessage);
        }

        return length;
    }
}

/// <summary>
/// The ground-contact point of a detection: the bottom-center of its box (X + W/2, Y + H).
/// Zone, boundary, and dwell rules decide on this point, never on the box centroid.
/// Each coordinate is a finite double in normalized 0..1 inclusive: the foot of an edge
/// box is legitimately exactly 0 or 1. The same 1e-9 slack as <see cref="BoundingBox"/>
/// applies so the foot point of a slack-passing box (Y marginally above 1) stays
/// representable instead of throwing; zone checks (Task 3) must tolerate that.
/// </summary>
public sealed record TrackedPoint(double X, double Y)
{
    /// <summary>
    /// Absolute slack matching <see cref="BoundingBox"/>: 1e-9 of frame extent (~6e-7 px
    /// at 640 px) absorbs double rounding so edge feet (exactly 0/1 through floats) survive.
    /// </summary>
    private const double PointTolerance = 1e-9;

    // Backing fields + explicit init setters (see Frame): `point with { X = 2 }` must throw
    // exactly as construction does.
    private readonly double _x = ValidateCoordinate(X, nameof(X));
    private readonly double _y = ValidateCoordinate(Y, nameof(Y));

    public double X { get => _x; init => _x = ValidateCoordinate(value, nameof(X)); }

    public double Y { get => _y; init => _y = ValidateCoordinate(value, nameof(Y)); }

    private static double ValidateCoordinate(double value, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < -PointTolerance || value > 1.0 + PointTolerance)
        {
            throw new ArgumentOutOfRangeException(name, "Tracked point coordinates must be finite values in 0..1 (within 1e-9 rounding slack).");
        }

        return value;
    }
}

/// <summary>
/// A person detection on a frame.
/// <see cref="Confidence"/> is a detector output signal used for thresholding only —
/// it is NOT a danger probability and must never be presented as one.
/// Only person detections are representable: <see cref="ClassId"/> must equal
/// <see cref="PersonClassId"/>.
/// </summary>
public sealed record Detection(BoundingBox Box, double Confidence, int ClassId)
{
    /// <summary>Class id reserved for persons; the only class this pipeline tracks.</summary>
    public const int PersonClassId = 0;

    /// <summary>
    /// Absolute slack for the confidence range check, matching the box-coordinate slack:
    /// a detector output at the exact boundary must survive float rounding.
    /// NaN and infinities are always rejected; -0.0 is accepted (it equals 0).
    /// </summary>
    private const double ConfidenceTolerance = 1e-9;

    // Backing fields + explicit init setters (see Frame):
    // `new Detection(...) { Confidence = 99 }` must throw exactly as construction does.
    private readonly BoundingBox _box = ValidateBox(Box);
    private readonly double _confidence = ValidateConfidence(Confidence);
    private readonly int _classId = ValidateClassId(ClassId);

    public BoundingBox Box { get => _box; init => _box = ValidateBox(value); }

    public double Confidence { get => _confidence; init => _confidence = ValidateConfidence(value); }

    public int ClassId { get => _classId; init => _classId = ValidateClassId(value); }

    /// <summary>Foot point of this detection: bottom-center of <see cref="Box"/>.</summary>
    public TrackedPoint FootPoint => new(Box.X + Box.W / 2.0, Box.Y + Box.H);

    private static BoundingBox ValidateBox(BoundingBox? box) =>
        box ?? throw new ArgumentNullException(nameof(Box));

    private static double ValidateConfidence(double confidence)
    {
        if (double.IsNaN(confidence) || double.IsInfinity(confidence) || confidence < -ConfidenceTolerance || confidence > 1.0 + ConfidenceTolerance)
        {
            throw new ArgumentOutOfRangeException(nameof(Confidence), "Confidence must be a finite value in 0..1 (within 1e-9 rounding slack).");
        }

        return confidence;
    }

    private static int ValidateClassId(int classId) =>
        classId != PersonClassId
            ? throw new ArgumentException($"Only person detections (ClassId == {PersonClassId}) are supported.", nameof(ClassId))
            : classId;
}
