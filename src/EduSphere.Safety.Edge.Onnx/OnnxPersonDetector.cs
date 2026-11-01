// CI-gated: requires Microsoft.ML.OnnxRuntime (see .csproj comment). NOT compiled in-sandbox.
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using EduSphere.Safety.Edge;

namespace EduSphere.Safety.Edge.Onnx;

/// <summary>
/// <see cref="IPersonDetector"/> backed by an ONNX person model (default: YOLOX-S COCO)
/// via Microsoft.ML.OnnxRuntime CPU inference. Thin glue by design: resampling lives in
/// <see cref="ImageSampling"/>, letterbox math in <see cref="Letterbox"/>, suppression in
/// <see cref="Nms"/>, output decoding in <see cref="IModelOutputParser"/> — all tested
/// without a model. This class only wires pixels → tensor → session → parser →
/// <see cref="Detection"/>.
/// <para>
/// PREPROCESS (must match the downloaded model card; benchmark freeze re-verifies):
/// <see cref="Frame.Pixels"/> (Rgb24 row-major) → pure-C# bilinear letterbox resample
/// (<see cref="ImageSampling.LetterboxRgb24ToNchwFloat"/>) onto a square canvas padded
/// with <see cref="LetterboxPad"/> gray → NCHW float32 tensor with values
/// <c>byte / pixelDivisor</c> (YOLOX-style raw pixels: divisor 1.0; use 255.0 for 0..1
/// exports). No mean/std shift. No SkiaSharp anywhere in this path.
/// </para>
/// </summary>
public sealed class OnnxPersonDetector : IPersonDetector, IDisposable
{
    // --- Model-card constants (defaults for the YOLOX-S export; see YoloxOutputParser) ---
    /// <summary>Letterbox pad gray (YOLOX demo uses 114; yolox/utils/demo_utils.py).</summary>
    private const byte LetterboxPad = 114;

    /// <summary>NMS IoU threshold (YOLOX demo default 0.45).</summary>
    private const double NmsIouThreshold = 0.45;

    /// <summary>Cap on returned detections per frame.</summary>
    private const int MaxDetections = 100;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly IModelOutputParser _parser;
    private readonly int _inputSize;
    private readonly double _pixelDivisor;
    private readonly double _confidenceThreshold;
    private bool _disposed;

    /// <param name="modelPath">Path to the .onnx weights file (must exist).</param>
    /// <param name="expectedSha256Hex">Expected SHA256 of the weights (hex); mismatch ⇒ refuse to start.</param>
    /// <param name="pixelDivisor">
    /// REQUIRED raw-pixel divisor (finite, > 0, no default): tensor values are
    /// <c>byte / pixelDivisor</c>, recorded at the benchmark freeze alongside the weights
    /// provenance (use 1.0 for raw 0..255 YOLOX exports, 255.0 for 0..1 exports).
    /// </param>
    /// <param name="inputSize">Square model input edge in px (must be the parser's supported size).</param>
    /// <param name="personClassIndex">COCO class index treated as person (0 = person).</param>
    /// <param name="confidenceThreshold">Detector signal threshold in 0..1 (NOT a danger probability).</param>
    /// <param name="intraOpThreads">ONNX intra-op thread pool size (≥ 1).</param>
    /// <exception cref="InvalidDataException">
    /// Weights checksum mismatch (refuse to start), or the model does not expose exactly
    /// one input.
    /// </exception>
    public OnnxPersonDetector(
        string modelPath,
        string expectedSha256Hex,
        double pixelDivisor,
        int inputSize = YoloxOutputParser.SupportedInputSize,
        int personClassIndex = 0,
        double confidenceThreshold = 0.5,
        int intraOpThreads = 1)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            throw new ArgumentException("Model path must not be empty.", nameof(modelPath));
        }

        if (string.IsNullOrWhiteSpace(expectedSha256Hex))
        {
            throw new ArgumentException("Expected SHA256 must not be empty.", nameof(expectedSha256Hex));
        }

        if (double.IsNaN(pixelDivisor) || double.IsInfinity(pixelDivisor) || pixelDivisor <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelDivisor), "Pixel divisor must be a finite value greater than 0.");
        }

        // The parser decodes self-describing rows at any anchor count, so any
        // stride-aligned square size is acceptable; the benchmark freeze
        // re-verifies the exact export (reference: 640px). YOLOX downsamples by
        // 32, so non-multiples of 32 would break the export's own grid math.
        if (inputSize < 256 || inputSize > 1280 || inputSize % 32 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputSize), "Input size must be a multiple of 32 in 256..1280.");
        }

        if (double.IsNaN(confidenceThreshold) || confidenceThreshold < 0.0 || confidenceThreshold > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(confidenceThreshold), "Confidence threshold must be in 0..1.");
        }

        if (intraOpThreads < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(intraOpThreads), "Intra-op threads must be at least 1.");
        }

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("ONNX weights file not found.", modelPath);
        }

        string actual;
        using (var weights = File.OpenRead(modelPath))
        {
            actual = Convert.ToHexString(SHA256.HashData(weights));
        }

        if (!string.Equals(actual, expectedSha256Hex.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"ONNX weights checksum mismatch for '{modelPath}': file is NOT the benchmark-frozen model. Refusing to start.");
        }

        _inputSize = inputSize;
        _pixelDivisor = pixelDivisor;
        _confidenceThreshold = confidenceThreshold;
        _parser = new YoloxOutputParser(personClassIndex);

        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = intraOpThreads,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING,
        };
        var session = new InferenceSession(modelPath, options);
        if (session.InputMetadata.Count != 1)
        {
            var count = session.InputMetadata.Count;
            session.Dispose();
            throw new InvalidDataException(
                $"ONNX model '{modelPath}' must expose exactly one input (found {count}).");
        }

        _session = session;
        _inputName = _session.InputMetadata.Keys.First();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Detection>> DetectAsync(Frame frame, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        if (frame.Pixels.IsEmpty)
        {
            throw new InvalidOperationException("OnnxPersonDetector requires frame pixels (Rgb24); scripted frames carry none.");
        }

        var letterbox = Letterbox.Compute(frame.Width, frame.Height, _inputSize, _inputSize);
        var nchw = ImageSampling.LetterboxRgb24ToNchwFloat(
            frame.Pixels.Span, frame.Width, frame.Height, letterbox, LetterboxPad, _pixelDivisor);

        var tensor = new DenseTensor<float>(new[] { 1, 3, _inputSize, _inputSize });
        for (var c = 0; c < 3; c++)
        {
            for (var y = 0; y < _inputSize; y++)
            {
                for (var x = 0; x < _inputSize; x++)
                {
                    tensor[0, c, y, x] = nchw[(c * _inputSize * _inputSize) + (y * _inputSize) + x];
                }
            }
        }

        var input = NamedOnnxValue.CreateFromTensor(_inputName, tensor);
        using var results = _session.Run(new[] { input });
        if (results.Count != 1)
        {
            throw new InvalidDataException(
                $"ONNX model must return exactly one output tensor (got {results.Count}).");
        }

        var output = results.First().AsTensor<float>();

        var candidates = _parser.Parse(
            output.ToArray(), output.Dimensions.ToArray(),
            letterbox, frame.Width, frame.Height, _confidenceThreshold);

        var boxes = candidates.Select(c => c.Box).ToList();
        var scores = candidates.Select(c => c.Score).ToList();
        var kept = Nms.Suppress(boxes, scores, NmsIouThreshold, _confidenceThreshold, MaxDetections);

        IReadOnlyList<Detection> detections = kept
            .Select(i => new Detection(boxes[i], scores[i], Detection.PersonClassId))
            .ToList();
        return Task.FromResult(detections);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _session.Dispose();
        }
    }
}
