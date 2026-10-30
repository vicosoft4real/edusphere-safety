namespace EduSphere.Safety.Edge;

/// <summary>
/// Output parser for YOLOX-S (COCO) ONNX exports, e.g. the Apache-2.0 export from
/// https://github.com/Megvii-BaseDetection/YOLOX (see docs/edge-benchmark.md for the
/// frozen weights provenance). Every magic number below cites its source; any number
/// NOT cited is a per-export assumption the benchmark freeze MUST re-verify against
/// the downloaded model card before trusting detections.
/// </summary>
public sealed class YoloxOutputParser : IModelOutputParser
{
    // --- Model-card constants (YOLOX-S, 640 input; Megvii YOLOX repo, yolox/models/yolox.py + demo) ---
    /// <summary>Input resolutions this parser supports (square edge length in px).</summary>
    public const int SupportedInputSize = 640;

    // Grid cells per stride level at 640 px: (640/8)^2 + (640/16)^2 + (640/32)^2
    // = 80*80 + 40*40 + 20*20 = 6400 + 1600 + 400 = 8400 anchors. Source: YOLOX
    // decoupled-head design (strides 8, 16, 32; yolox/models/yolo_head.py).
    // This documents the reference 640px export; the shape check below deliberately
    // accepts ANY anchor count > 0 so stride-variant exports (different input sizes,
    // pruned heads) still decode — the rows are self-describing center-format boxes
    // (cx, cy, w, h in canvas pixels), so no per-stride grid split is needed here.
    private const int ExpectedAnchors = 8400;

    // Per-anchor layout: cx, cy, w, h (letterboxed-canvas pixels) + 1 objectness
    // + 80 COCO class scores. Source: YOLOX demo output decoding (demo/MegEngine/...,
    // yolox/utils/demo_utils.py "postprocess": output[..., 4] objectness * class).
    private const int ChannelsPerAnchor = 85;
    private const int BoxFields = 4;
    private const int ObjectnessIndex = 4;
    private const int ClassCount = 80;

    /// <summary>
    /// Default cap on candidates forwarded per frame. Applied AFTER the score sort
    /// (score-order top-K), so a crowded frame keeps its highest-scoring boxes.
    /// </summary>
    public const int DefaultMaxCandidates = 1000;

    /// <inheritdoc />
    public string ModelCard => "YOLOX-S COCO 640 (Megvii YOLOX, Apache-2.0)";

    /// <summary>COCO class index treated as "person" (COCO id 0 = person).</summary>
    public int PersonClassIndex { get; }

    /// <summary>Maximum candidates returned per frame (applied after the score sort).</summary>
    public int MaxCandidates { get; }

    public YoloxOutputParser(int personClassIndex = 0, int maxCandidates = DefaultMaxCandidates)
    {
        if (personClassIndex < 0 || personClassIndex >= ClassCount)
        {
            throw new ArgumentOutOfRangeException(nameof(personClassIndex), "Person class index must be a COCO class 0..79.");
        }

        if (maxCandidates < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCandidates), "Max candidates must be at least 1.");
        }

        PersonClassIndex = personClassIndex;
        MaxCandidates = maxCandidates;
    }

    /// <inheritdoc />
    public IReadOnlyList<(BoundingBox Box, double Score)> Parse(
        float[] output,
        int[] dims,
        LetterboxParams letterbox,
        int srcWidth,
        int srcHeight,
        double confidenceThreshold)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(dims);
        ArgumentNullException.ThrowIfNull(letterbox);

        // Shape rejection: rank 3, batch 1, 85 channels, at least one anchor. The anchor
        // count is NOT pinned to ExpectedAnchors (8400): stride-variant exports decode
        // through the same self-describing rows. Assumption (re-verify per export):
        // batch dim first, anchors second, single batch.
        if (dims.Length != 3 || dims[0] != 1 || dims[2] != ChannelsPerAnchor || dims[1] <= 0)
        {
            throw new InvalidDataException(
                $"Unexpected YOLOX output shape [{string.Join(", ", dims)}]; " +
                $"expected [1, anchors, {ChannelsPerAnchor}] with anchors > 0 " +
                $"(reference {SupportedInputSize}px export: [1, {ExpectedAnchors}, {ChannelsPerAnchor}]).");
        }

        var anchors = dims[1];
        if (output.Length != (long)anchors * ChannelsPerAnchor)
        {
            throw new InvalidDataException(
                $"YOLOX output length {output.Length} does not match shape [1, {anchors}, {ChannelsPerAnchor}].");
        }

        if (srcWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(srcWidth), "Source width must be positive.");
        }

        if (srcHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(srcHeight), "Source height must be positive.");
        }

        if (double.IsNaN(letterbox.Scale) || double.IsInfinity(letterbox.Scale) || letterbox.Scale <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(letterbox), "Letterbox scale must be a finite value greater than 0.");
        }

        var passing = new List<(BoundingBox Box, double Score, int Index)>();
        for (var a = 0; a < anchors; a++)
        {
            var row = a * ChannelsPerAnchor;
            var cx = output[row];
            var cy = output[row + 1];
            var w = output[row + 2];
            var h = output[row + 3];
            var objectness = output[row + ObjectnessIndex];
            if (!IsFinite(cx) || !IsFinite(cy) || !IsFinite(w) || !IsFinite(h) || !IsFinite(objectness))
            {
                continue; // Non-finite anchor: skip, never throw.
            }

            if (w <= 0.0f || h <= 0.0f)
            {
                continue;
            }

            // Person = argmax class is the person index; a box whose best class is a
            // non-person class is dropped even when its person score alone would pass.
            var bestClass = -1;
            var bestClassScore = float.NegativeInfinity;
            var finiteClasses = true;
            for (var c = 0; c < ClassCount; c++)
            {
                var classScore = output[row + BoxFields + 1 + c];
                if (!IsFinite(classScore))
                {
                    finiteClasses = false;
                    break;
                }

                if (classScore > bestClassScore)
                {
                    bestClassScore = classScore;
                    bestClass = c;
                }
            }

            if (!finiteClasses || bestClass != PersonClassIndex)
            {
                continue;
            }

            var score = (double)objectness * bestClassScore; // Source: YOLOX postprocess multiplies obj * cls.
            if (!double.IsFinite(score) || score < confidenceThreshold)
            {
                continue;
            }

            // Center-format box in letterboxed-canvas pixels -> corners.
            var (x, y, bw, bh) = letterbox.MapBoxToSourceNormalized(
                cx - w / 2.0, cy - h / 2.0, w, h, srcWidth, srcHeight);

            // Clamp pad-overflow into the frame; drop boxes with nothing inside.
            var x0 = Math.Max(0.0, x);
            var y0 = Math.Max(0.0, y);
            var x1 = Math.Min(1.0, x + bw);
            var y1 = Math.Min(1.0, y + bh);
            if (x1 - x0 <= 0.0 || y1 - y0 <= 0.0)
            {
                continue;
            }

            passing.Add((new BoundingBox(x0, y0, x1 - x0, y1 - y0), score, a));
        }

        // Score-order top-K: highest scores win; ascending anchor index breaks ties so
        // the output order is fully deterministic.
        return passing
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Index)
            .Take(MaxCandidates)
            .Select(c => (c.Box, c.Score))
            .ToList();
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
