# EduSphere.Safety.Edge.Onnx — CI-gated ONNX detector adapter

This project implements `IPersonDetector` on Microsoft.ML.OnnxRuntime (CPU) for the
YOLOX-S COCO export. It **cannot be restored or compiled in the sandbox**: the
`Microsoft.ML.OnnxRuntime` package is not in the sandbox package cache and there is
no network. It is therefore **excluded from `EduSphere.Safety.slnx`** so the sandbox
build stays green. All logic that CAN be tested in-sandbox (letterbox math
`Letterbox`, pixel resampling `ImageSampling`, suppression `Nms`, output decoding
`YoloxOutputParser` behind `IModelOutputParser`) lives in `EduSphere.Safety.Edge`
with committed tests (`EdgePreprocessTests`, `EdgeParserTests`); this project is thin
glue only (session, weights checksum, tensor fill). SkiaSharp is NOT referenced here
(Simulator only) — detection-path resampling is pure C# bilinear in Edge.

## CI steps (connected runner only)

```sh
# 1. Confirm the OR pin exists; update it if the version was withdrawn:
grep Microsoft.ML.OnnxRuntime Directory.Packages.props
# 2. Re-include this project in the solution:
dotnet sln EduSphere.Safety.slnx add src/EduSphere.Safety.Edge.Onnx/EduSphere.Safety.Edge.Onnx.csproj
# 3. Restore, build, and review warnings (TreatWarningsAsErrors is on):
dotnet restore --source <connected-feed>
dotnet build src/EduSphere.Safety.Edge.Onnx -c Release
# 4. Run the benchmark per docs/edge-benchmark.md (downloads + verifies weights first).
```

After a green CI run, do NOT commit the `slnx` change from a sandbox checkout —
re-adding must happen on (or be verified by) the connected runner.

## Files

- `OnnxPersonDetector.cs` — `IPersonDetector` + `IDisposable`. Startup: file-exists +
  SHA256 verify streamed via `SHA256.HashData(File.OpenRead(...))` (mismatch ⇒
  `InvalidDataException`, refuse to start); `InferenceSession` with `SessionOptions`
  (`GraphOptimizationLevel.All`, `IntraOpNumThreads`, `LogSeverityLevel`);
  single-input validation (`InputMetadata.Count == 1`). Ctor takes a REQUIRED
  `pixelDivisor` (finite > 0, no default — recorded at the benchmark freeze).
  Per frame: `Letterbox.Compute` → `ImageSampling` bilinear resample onto a 114-gray
  square canvas → plain-loop fill of the NCHW float32 `DenseTensor` → `Session.Run`
  (single-output validation) → `Edge.YoloxOutputParser` → `Edge.Nms` → `Detection`
  list (person class only). `using var` on the single `NamedOnnxValue` and the
  results (a `List<T>` is not disposable — never a using-list).
- Parsing (`IModelOutputParser` + `YoloxOutputParser`) lives in
  `EduSphere.Safety.Edge` (no OR types): YOLOX-S `[1, anchors, 85]` decoding
  (strides 8/16/32 → 8400 anchors documented for the 640 reference export, but any
  anchor count > 0 is accepted; obj × cls score with argmax-is-person gating;
  score-order top-K, non-finite anchors skipped, clamp-then-drop, shape rejection).
  Every magic number cites its source; unexpected shapes throw `InvalidDataException`.

## ONNX Runtime 1.x API surface used (for the reviewer)

`Microsoft.ML.OnnxRuntime.InferenceSession(string, SessionOptions)`,
`SessionOptions.GraphOptimizationLevel` (`GraphOptimizationLevel.All`),
`SessionOptions.IntraOpNumThreads`, `SessionOptions.LogSeverityLevel`
(`OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING`), `InferenceSession.InputMetadata`
(input name via `.Keys.First()`), `NamedOnnxValue.CreateFromTensor(name, tensor)`,
`InferenceSession.Run(IReadOnlyCollection<NamedOnnxValue>)`,
`DisposableNamedOnnxValue.AsTensor<float>()` (`Tensor<float>.Dimensions`,
LINQ `ToArray()`), `Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>` with
`tensor[0, c, y, x]` indexing, `SHA256.HashData(Stream)`.

## Sandbox verification status

NOT compiled here (package absent — deliberate). Verified by API review against OR
1.x knowledge (API list above), pure-helper tests in `EdgePreprocessTests` +
`EdgeParserTests`, and the `/tmp` probe matrix. First connected-CI build is the
real check.
