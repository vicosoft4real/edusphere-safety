using SkiaSharp;

namespace EduSphere.Simulator;

/// <summary>
/// SkiaSharp scene renderer: static corridor background + filled person rects +
/// a font-free frame-counter burn-in, encoded as PNGs.
/// <para>
/// The burn-in is a 16-block binary rendering of the frame index (top-left, white
/// border) — deliberately NOT text, so rendering needs no fonts and no
/// HarfBuzz/text-shaping packages; output stays byte-deterministic per (seed, frame).
/// Person fill colors come from a fixed palette indexed by person order.
/// </para>
/// </summary>
public static class SceneRenderer
{
    private static readonly SKColor[] Palette =
    [
        new(214, 48, 49),   // person 0: red
        new(46, 134, 193),  // person 1: blue
        new(39, 174, 96),   // person 2: green
        new(243, 156, 18),  // person 3: orange
    ];

    /// <summary>Renders one scene frame to a 24-bit SKBitmap.</summary>
    public static SKBitmap RenderFrame(SceneScript script, SceneFrame frame)
    {
        var bitmap = new SKBitmap(script.Width, script.Height);
        using var canvas = new SKCanvas(bitmap);
        DrawBackground(canvas, script);
        for (var i = 0; i < frame.Boxes.Count; i++)
        {
            DrawPerson(canvas, script, frame.Boxes[i], Palette[i % Palette.Length]);
        }

        DrawFrameCounter(canvas, frame.FrameIndex);
        return bitmap;
    }

    /// <summary>Renders every present frame of <paramref name="script"/> as PNGs into <paramref name="directory"/>.</summary>
    public static void RenderAll(SceneScript script, string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var frame in script.Frames)
        {
            using var bitmap = RenderFrame(script, frame);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.OpenWrite(Path.Combine(directory, SceneScript.FrameFileName(frame.FrameIndex)));
            data.SaveTo(stream);
        }
    }

    private static void DrawBackground(SKCanvas canvas, SceneScript script)
    {
        var w = script.Width;
        var h = script.Height;
        // Wall (top 55%) / floor (bottom 45%) + skirting line: a static corridor.
        canvas.Clear(new SKColor(168, 178, 186));
        using (var floor = new SKPaint { Color = new SKColor(112, 122, 130) })
        {
            canvas.DrawRect(0, h * 0.55f, w, h * 0.45f, floor);
        }

        using (var line = new SKPaint { Color = new SKColor(90, 98, 106), StrokeWidth = 3 })
        {
            canvas.DrawLine(0, (float)(h * 0.55), w, (float)(h * 0.55), line);
        }

        // Seeded speckle (LCG) so the background is static per seed but not flat.
        // Positions derive from the seed only — identical on every frame.
        var state = unchecked((uint)(script.Seed * 1664525L + 1013904223L));
        using var dot = new SKPaint { Color = new SKColor(150, 160, 168) };
        for (var i = 0; i < 220; i++)
        {
            state = unchecked(state * 1664525u + 1013904223u);
            var x = (state >> 8) % (uint)w;
            state = unchecked(state * 1664525u + 1013904223u);
            var y = (state >> 8) % (uint)h;
            canvas.DrawRect((int)x, (int)y, 2, 2, dot);
        }
    }

    private static void DrawPerson(SKCanvas canvas, SceneScript script, PersonBox box, SKColor color)
    {
        var rect = new SKRect(
            (float)(box.X * script.Width),
            (float)(box.Y * script.Height),
            (float)((box.X + box.W) * script.Width),
            (float)((box.Y + box.H) * script.Height));
        using var fill = new SKPaint { Color = color, Style = SKPaintStyle.Fill };
        canvas.DrawRect(rect, fill);
        using var edge = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
        canvas.DrawRect(rect, edge);
    }

    private static void DrawFrameCounter(SKCanvas canvas, int frameIndex)
    {
        const int bits = 16;
        const int cell = 10;
        const int pad = 8;
        using var border = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
        using var on = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
        using var off = new SKPaint { Color = new SKColor(40, 40, 40), Style = SKPaintStyle.Fill };
        var total = (bits * cell) + (pad * 2);
        canvas.DrawRect(pad - 3, pad - 3, total + 6, cell + 6, border);
        for (var b = 0; b < bits; b++)
        {
            var set = ((frameIndex >> (bits - 1 - b)) & 1) != 0;
            canvas.DrawRect(pad + (b * cell), pad, cell - 1, cell, set ? on : off);
        }
    }
}
