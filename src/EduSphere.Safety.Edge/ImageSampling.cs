namespace EduSphere.Safety.Edge;

/// <summary>
/// Pure detector preprocessing: letterboxed resampling of Rgb24 source pixels into an
/// NCHW float32 tensor layout. No IO, no native dependencies — SkiaSharp is NOT used in
/// the detection path (Simulator rendering only). Interpolation parity (this bilinear)
/// is recorded at the benchmark freeze (docs/edge-benchmark.md §2).
/// </summary>
public static class ImageSampling
{
    /// <summary>
    /// Resamples <paramref name="rgb24"/> (Rgb24 row-major, <c>Length == srcW * srcH * 3</c>)
    /// onto the canvas described by <paramref name="letterbox"/> and returns the canvas as
    /// NCHW float32 (channel-major: <c>[(c * targetH + y) * targetW + x]</c>) with values
    /// <c>byte / divisor</c>.
    /// <para>
    /// Each canvas pixel (x, y) inside the pasted image rectangle samples the source at
    /// ((x − PadLeft) / Scale, (y − PadTop) / Scale) with bilinear interpolation over the
    /// four surrounding texels. Canvas pixels outside the pasted rectangle (the letterbox
    /// pad) emit <paramref name="padGray"/> on every channel. Source coordinates are
    /// clamped into the texel grid before sampling, so a rounded scaled edge never grows a
    /// gray fringe inside the image.
    /// </para>
    /// <para>
    /// Exactness: a canvas pixel mapping to an exact integer texel coordinate reproduces
    /// that texel bit-for-bit (up to the final <c>/ divisor</c> rounding, which matches a
    /// direct <c>(float)(byte / divisor)</c> computation), so 1:1 letterboxes are identities.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="srcW"/>/<paramref name="srcH"/> or the canvas dimensions are not
    /// positive, <paramref name="divisor"/> is not finite-positive, or the letterbox scale
    /// is not finite-positive.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="rgb24"/> length differs from <c>srcW * srcH * 3</c>.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="letterbox"/> is null.</exception>
    public static float[] LetterboxRgb24ToNchwFloat(
        ReadOnlySpan<byte> rgb24,
        int srcW,
        int srcH,
        LetterboxParams letterbox,
        byte padGray,
        double divisor)
    {
        ArgumentNullException.ThrowIfNull(letterbox);

        if (srcW <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(srcW), "Source width must be positive.");
        }

        if (srcH <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(srcH), "Source height must be positive.");
        }

        if (rgb24.Length != (long)srcW * srcH * 3)
        {
            throw new ArgumentException(
                $"Pixel payload must be Rgb24 row-major (Length == srcW * srcH * 3 == {(long)srcW * srcH * 3}).",
                nameof(rgb24));
        }

        if (double.IsNaN(divisor) || double.IsInfinity(divisor) || divisor <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(divisor), "Pixel divisor must be a finite value greater than 0.");
        }

        if (double.IsNaN(letterbox.Scale) || double.IsInfinity(letterbox.Scale) || letterbox.Scale <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(letterbox), "Letterbox scale must be a finite value greater than 0.");
        }

        var targetW = letterbox.TargetWidth;
        var targetH = letterbox.TargetHeight;
        if (targetW <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(letterbox), "Letterbox target width must be positive.");
        }

        if (targetH <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(letterbox), "Letterbox target height must be positive.");
        }

        var padValue = (float)(padGray / divisor);
        var output = new float[3 * targetH * targetW];

        var imageRight = letterbox.PadLeft + letterbox.ScaledWidth;
        var imageBottom = letterbox.PadTop + letterbox.ScaledHeight;
        var maxSx = (double)(srcW - 1);
        var maxSy = (double)(srcH - 1);

        for (var y = 0; y < targetH; y++)
        {
            var inImageRow = y >= letterbox.PadTop && y < imageBottom;
            for (var x = 0; x < targetW; x++)
            {
                float r, g, b;
                if (inImageRow && x >= letterbox.PadLeft && x < imageRight)
                {
                    var sx = (x - letterbox.PadLeft) / letterbox.Scale;
                    var sy = (y - letterbox.PadTop) / letterbox.Scale;
                    sx = Math.Min(Math.Max(sx, 0.0), maxSx);
                    sy = Math.Min(Math.Max(sy, 0.0), maxSy);

                    var x0 = (int)Math.Floor(sx);
                    var y0 = (int)Math.Floor(sy);
                    var x1 = Math.Min(x0 + 1, srcW - 1);
                    var y1 = Math.Min(y0 + 1, srcH - 1);
                    var fx = sx - x0;
                    var fy = sy - y0;

                    var o00 = (y0 * srcW + x0) * 3;
                    var o10 = (y0 * srcW + x1) * 3;
                    var o01 = (y1 * srcW + x0) * 3;
                    var o11 = (y1 * srcW + x1) * 3;

                    var rTop = ((1.0 - fx) * rgb24[o00]) + (fx * rgb24[o10]);
                    var rBottom = ((1.0 - fx) * rgb24[o01]) + (fx * rgb24[o11]);
                    var gTop = ((1.0 - fx) * rgb24[o00 + 1]) + (fx * rgb24[o10 + 1]);
                    var gBottom = ((1.0 - fx) * rgb24[o01 + 1]) + (fx * rgb24[o11 + 1]);
                    var bTop = ((1.0 - fx) * rgb24[o00 + 2]) + (fx * rgb24[o10 + 2]);
                    var bBottom = ((1.0 - fx) * rgb24[o01 + 2]) + (fx * rgb24[o11 + 2]);

                    r = (float)(((1.0 - fy) * rTop + (fy * rBottom)) / divisor);
                    g = (float)(((1.0 - fy) * gTop + (fy * gBottom)) / divisor);
                    b = (float)(((1.0 - fy) * bTop + (fy * bBottom)) / divisor);
                }
                else
                {
                    r = g = b = padValue;
                }

                output[y * targetW + x] = r;
                output[(targetH * targetW) + (y * targetW + x)] = g;
                output[(2 * targetH * targetW) + (y * targetW + x)] = b;
            }
        }

        return output;
    }
}
