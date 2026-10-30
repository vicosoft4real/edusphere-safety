namespace EduSphere.Safety.Edge;

/// <summary>
/// Aspect-preserving ("letterbox") resize parameters: the source frame is scaled by
/// <see cref="Scale"/> to <see cref="ScaledWidth"/>x<see cref="ScaledHeight"/> and centered
/// on a <c>TargetWidth</c>x<c>TargetHeight</c> canvas, leaving
/// (<see cref="PadLeft"/>, <see cref="PadTop"/>) pad pixels on the left/top.
/// Any leftover odd pixel goes to the right/bottom (integer division truncation),
/// so pad-right/bottom = target - scaled - pad-left/top.
/// Pure dimension math — no pixels. Used by the ONNX adapter (Task 6) for both
/// preprocessing (where to paste the resized frame) and postprocessing (how to map
/// letterboxed model boxes back to source coordinates).
/// </summary>
public sealed record LetterboxParams(
    double Scale,
    int ScaledWidth,
    int ScaledHeight,
    int PadLeft,
    int PadTop,
    int TargetWidth,
    int TargetHeight)
{
    /// <summary>
    /// Maps a point in letterboxed-canvas pixels (0..TargetWidth, 0..TargetHeight)
    /// back to source-frame pixels. No clamping: points inside the pad report
    /// out-of-range source coordinates and the caller decides (clamp or drop).
    /// </summary>
    public (double X, double Y) MapToSourcePixels(double x, double y) =>
        ((x - PadLeft) / Scale, (y - PadTop) / Scale);

    /// <summary>
    /// Maps a box in letterboxed-canvas pixels to source-NORMALIZED coordinates
    /// (fractions of <paramref name="srcWidth"/>/<paramref name="srcHeight"/>).
    /// No clamping or validation: the caller clamps to 0..1 and drops degenerate
    /// boxes (the adapter does this before constructing <see cref="BoundingBox"/>).
    /// </summary>
    public (double X, double Y, double W, double H) MapBoxToSourceNormalized(
        double x, double y, double w, double h, int srcWidth, int srcHeight)
    {
        var (x0, y0) = MapToSourcePixels(x, y);
        var (x1, y1) = MapToSourcePixels(x + w, y + h);
        return (x0 / srcWidth, y0 / srcHeight, (x1 - x0) / srcWidth, (y1 - y0) / srcHeight);
    }
}

/// <summary>Pure letterbox parameter computation (see <see cref="LetterboxParams"/>).</summary>
public static class Letterbox
{
    /// <summary>
    /// Computes letterbox parameters for fitting <c>srcWidth</c>x<c>srcHeight</c>
    /// into <c>dstWidth</c>x<c>dstHeight</c>. All dimensions must be positive.
    /// Scaled dimensions use banker's-agnostic <see cref="Math.Round(double)"/>
    /// (round-half-to-even) so near-exact fits (e.g. 641 px source at scale
    /// 640/641 = 639.9999… scaled px) land on 640 instead of truncating to 639.
    /// </summary>
    public static LetterboxParams Compute(int srcWidth, int srcHeight, int dstWidth, int dstHeight)
    {
        if (srcWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(srcWidth), "Source width must be positive.");
        }

        if (srcHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(srcHeight), "Source height must be positive.");
        }

        if (dstWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dstWidth), "Target width must be positive.");
        }

        if (dstHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dstHeight), "Target height must be positive.");
        }

        var scale = Math.Min((double)dstWidth / srcWidth, (double)dstHeight / srcHeight);
        var scaledWidth = Math.Max(1, (int)Math.Round(srcWidth * scale));
        var scaledHeight = Math.Max(1, (int)Math.Round(srcHeight * scale));
        return new LetterboxParams(
            scale,
            scaledWidth,
            scaledHeight,
            (dstWidth - scaledWidth) / 2,
            (dstHeight - scaledHeight) / 2,
            dstWidth,
            dstHeight);
    }
}
