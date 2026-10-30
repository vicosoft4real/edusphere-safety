namespace EduSphere.Safety.Edge;

/// <summary>
/// Intersection-over-union non-maximum suppression over person boxes.
/// Pure function on normalized <see cref="BoundingBox"/> values — no pixels, no model
/// types — so it is unit-testable in-sandbox and shared by the ONNX adapter (Task 6).
/// </summary>
public static class Nms
{
    /// <summary>
    /// Intersection over union of two normalized boxes, in 0..1.
    /// Boxes are non-degenerate by construction (<see cref="BoundingBox"/> rejects
    /// zero-area), so the union is strictly positive and no divide-by-zero guard is needed.
    /// </summary>
    public static double IoU(BoundingBox a, BoundingBox b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var x0 = Math.Max(a.X, b.X);
        var y0 = Math.Max(a.Y, b.Y);
        var x1 = Math.Min(a.X + a.W, b.X + b.W);
        var y1 = Math.Min(a.Y + a.H, b.Y + b.H);
        var inter = Math.Max(0.0, x1 - x0) * Math.Max(0.0, y1 - y0);
        var union = (a.W * a.H) + (b.W * b.H) - inter;
        return inter / union;
    }

    /// <summary>
    /// Greedy IoU suppression. Returns the kept indexes into <paramref name="boxes"/>
    /// in selection order (score descending; ties broken by ascending input index,
    /// so the output order is fully deterministic).
    /// <list type="bullet">
    /// <item>Candidates with score strictly below <paramref name="scoreThreshold"/>
    /// are dropped first; a score exactly equal to the threshold is KEPT.</item>
    /// <item>Each selected box suppresses later candidates with IoU strictly greater
    /// than <paramref name="iouThreshold"/>; IoU exactly equal to the threshold is kept.
    /// (Exact duplicates have IoU 1 and are suppressed for any threshold below 1.)</item>
    /// <item>At most <paramref name="maxDetections"/> indexes are returned
    /// (highest scores win).</item>
    /// </list>
    /// Empty input yields an empty result (never null).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Either threshold is NaN, infinite, or outside 0..1, or
    /// <paramref name="maxDetections"/> is below 1.
    /// </exception>
    public static IReadOnlyList<int> Suppress(
        IReadOnlyList<BoundingBox> boxes,
        IReadOnlyList<double> scores,
        double iouThreshold,
        double scoreThreshold,
        int maxDetections)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        ArgumentNullException.ThrowIfNull(scores);

        if (boxes.Count != scores.Count)
        {
            throw new ArgumentException("Boxes and scores must have the same length.", nameof(scores));
        }

        if (double.IsNaN(iouThreshold) || double.IsInfinity(iouThreshold) || iouThreshold < 0.0 || iouThreshold > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(iouThreshold), "IoU threshold must be a finite value in 0..1.");
        }

        if (double.IsNaN(scoreThreshold) || double.IsInfinity(scoreThreshold) || scoreThreshold < 0.0 || scoreThreshold > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(scoreThreshold), "Score threshold must be a finite value in 0..1.");
        }

        if (maxDetections < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDetections), "Max detections must be at least 1.");
        }

        var order = Enumerable.Range(0, boxes.Count)
            .Where(i => scores[i] >= scoreThreshold)
            .OrderByDescending(i => scores[i])
            .ThenBy(i => i)
            .ToList();

        var kept = new List<int>(Math.Min(maxDetections, order.Count));
        foreach (var candidate in order)
        {
            if (kept.Count >= maxDetections)
            {
                break;
            }

            var suppressed = false;
            foreach (var keptIndex in kept)
            {
                if (IoU(boxes[keptIndex], boxes[candidate]) > iouThreshold)
                {
                    suppressed = true;
                    break;
                }
            }

            if (!suppressed)
            {
                kept.Add(candidate);
            }
        }

        return kept;
    }
}
