using System.Diagnostics;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;
using VisionStudio.AlgorithmBenchmark;

var diagnostics = VisionAlgorithmRuntime.Initialize();
var measurements = new List<object>();
foreach (var (width, height) in new[] { (640, 480), (1280, 960) })
{
    using var template = new Mat(48, 48, MatType.CV_8UC1);
    var bytes = new byte[48 * 48]; new Random(417).NextBytes(bytes); System.Runtime.InteropServices.Marshal.Copy(bytes, 0, template.Data, bytes.Length);
    using var scene = new Mat(height, width, MatType.CV_8UC1, Scalar.All(10));
    using (var region = new Mat(scene, new Rect(width / 2, height / 2, 48, 48))) template.CopyTo(region);
    using var source = VisionImage.Own(scene.Clone()); using var pattern = VisionImage.Own(template.Clone());
    var context = new NodeExecutionContext(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(source), ["template"] = VisionValue.Image(pattern) });
    var node = new NodeDefinition("match", "match.template", null, null, null);
    var executor = new TemplateMatchNode();
    var legacy = new LegacyTemplateMatchNode();
    void OpenCv() { using var result = new Mat(); Cv2.MatchTemplate(scene, template, result, TemplateMatchModes.CCoeffNormed); Cv2.MinMaxLoc(result, out _, out _, out _, out _); }
    void JlVision() { var result = executor.ExecuteAsync(context, node, default).GetAwaiter().GetResult(); if (!result.Outputs["found"].Require<bool>("found")) throw new InvalidOperationException("Benchmark target not found."); }
    void OldNode() => legacy.ExecuteAsync(context, node, default).GetAwaiter().GetResult();
    var cold = Stopwatch.StartNew(); JlVision(); cold.Stop();
    measurements.Add(new { width, height, templateWidth = 48, templateHeight = 48, iterations = 100, coldJLVisionMs = cold.Elapsed.TotalMilliseconds,
        openCvKernel = Measure(OpenCv), openCvNode = Measure(OldNode), jlVisionNode = Measure(JlVision) });

    var rotating = node with { Parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"angleRangeDeg":45,"angleStepDeg":5,"minScore":0.5}""") };
    void RotateOld() => legacy.ExecuteAsync(context, rotating, default).GetAwaiter().GetResult();
    void RotateNative() => executor.ExecuteAsync(context, rotating, default).GetAwaiter().GetResult();
    measurements.Add(new { width, height, rotationRangeDeg = 45, rotationStepDeg = 5, iterations = 100, openCvNode = Measure(RotateOld), jlVisionNode = Measure(RotateNative) });
}

using var shape = new Mat(160, 160, MatType.CV_8UC1, Scalar.All(0));
Cv2.FillPoly(shape, [new[] { new Point(35, 35), new Point(125, 35), new Point(125, 60), new Point(90, 60), new Point(90, 120), new Point(35, 120) }], Scalar.All(255));
Cv2.FindContours(shape, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
var moments = Cv2.Moments(contours.MaxBy(c => Cv2.ContourArea(c))!);
var origin = new Point2f((float)(moments.M10 / moments.M00), (float)(moments.M01 / moments.M00));
const double targetX = 319.4, targetY = 243.2, targetAngle = 33.7;
using var placed = new Mat(480, 640, MatType.CV_8UC1, Scalar.All(0));
using (var matrix = Cv2.GetRotationMatrix2D(origin, targetAngle, 1))
{ matrix.Set(0, 2, matrix.At<double>(0, 2) + targetX - origin.X); matrix.Set(1, 2, matrix.At<double>(1, 2) + targetY - origin.Y); Cv2.WarpAffine(shape, placed, matrix, placed.Size()); }
using var shapeInput = VisionImage.Own(shape.Clone()); using var sceneInput = VisionImage.Own(placed.Clone());
var poseResult = new ShapeMatchNode().ExecuteAsync(new(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(sceneInput), ["template"] = VisionValue.Image(shapeInput) }),
    new("pose", "match.shape", null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"angleRangeDeg":45,"angleStepDeg":5,"minScore":0.7}""")), default).GetAwaiter().GetResult();
using var displayed = (VisionImage)poseResult.Outputs["image"].Value!;
var pose = poseResult.Outputs["pose"].Require<VisionPose2D>("pose");
Console.WriteLine(JsonSerializer.Serialize(new { diagnostics, architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), processors = Environment.ProcessorCount,
    measurements, accuracy = new { expected = new { x = targetX, y = targetY, angle = targetAngle }, actual = pose, positionErrorPx = Math.Sqrt(Math.Pow(pose.X - targetX, 2) + Math.Pow(pose.Y - targetY, 2)), angleErrorDeg = Math.Abs(pose.ThetaDeg - targetAngle) },
    note = "Synthetic CPU benchmark; both node measurements include their actual preprocessing and result construction. OpenCV kernel is separately labeled. Warm JLVision nodes reuse cached models. No timing assertion is a test gate." }, new JsonSerializerOptions { WriteIndented = true }));
VisionAlgorithmRuntime.ClearModelCache();

static object Measure(Action action)
{
    for (var i = 0; i < 5; i++) action();
    var timings = new double[100];
    for (var i = 0; i < timings.Length; i++) { var watch = Stopwatch.StartNew(); action(); watch.Stop(); timings[i] = watch.Elapsed.TotalMilliseconds; }
    Array.Sort(timings); return new { medianMs = timings[50], p95Ms = timings[95] };
}
