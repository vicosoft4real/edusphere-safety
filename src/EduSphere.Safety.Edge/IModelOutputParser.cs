namespace EduSphere.Safety.Edge;

/// <summary>
/// Parses one decoded model output tensor into person candidates in SOURCE-frame
/// normalized coordinates. The interface speaks only in plain arrays
/// (<c>float[]</c> + row-major <c>dims</c>) — never ONNX Runtime tensor types —
/// so every parser is unit-testable without a model file or session.
/// </summary>
public interface IModelOutputParser
{
    /// <summary>Human-readable model card this parser implements (e.g. "YOLOX-S COCO 640").</summary>
    string ModelCard { get; }

    /// <summary>
    /// Parses <paramref name="output"/> (row-major, shape <paramref name="dims"/>) into
    /// (box, score) candidates with boxes in source-frame normalized 0..1 coordinates
    /// (via <paramref name="letterbox"/> un-mapping), already filtered to
    /// <paramref name="confidenceThreshold"/> and clamped inside the frame.
    /// Degenerate (zero-area after clamp) boxes are dropped, never returned.
    /// Candidates are returned sorted by score DESCENDING (highest first; ties broken by
    /// ascending anchor index for determinism) with the parser's top-K cap applied AFTER
    /// the sort — a crowded frame keeps its best boxes, never its first anchors.
    /// </summary>
    IReadOnlyList<(BoundingBox Box, double Score)> Parse(
        float[] output,
        int[] dims,
        LetterboxParams letterbox,
        int srcWidth,
        int srcHeight,
        double confidenceThreshold);
}
