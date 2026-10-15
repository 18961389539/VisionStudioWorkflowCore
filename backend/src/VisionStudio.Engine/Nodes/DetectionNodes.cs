using VisionStudio.Engine.Algorithms;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 斑点分析：measure.blobs。
/// 支持全部斑点筛选/排序，或只返回最大斑点；保留 DetectionList 与首选目标几何快捷输出。
/// 旧 measure.blob 节点继续注册，用于兼容已保存工作流。
/// 找不到任何目标不是错误（数量检查需要 count=0 的正常结果）；首选几何输出在无目标时缺省。
/// </summary>
public sealed class MultiBlobNode : IVisionNodeExecutor
{
    public string Type => "measure.blobs";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var sourceImage = context.RequireImage("image");
        var source = sourceImage.Mat;
        var mode = node.GetString("mode", "all").Trim().ToLowerInvariant();
        if (mode is not ("all" or "largest")) throw new InvalidOperationException($"Unsupported blob mode '{mode}'.");
        var largestOnly = mode == "largest";
        var minArea = node.GetDouble("minArea", 100);
        var maxArea = largestOnly ? 0 : node.GetDouble("maxArea", 0);
        var minCircularity = largestOnly ? 0 : node.GetDouble("minCircularity", 0);
        var maxCount = largestOnly ? 1 : Math.Clamp(node.GetInt("maxCount", 20), 1, 200);
        var sortBy = largestOnly ? "area" : node.GetString("sortBy", "area").Trim().ToLowerInvariant();
        var descending = largestOnly || node.GetBool("descending", true);
        var roi = node.GetRoi();

        var gray = PreprocessImageHelpers.ToGray8(source, out var converted, out var ownedGray);
        Mat? masked = null;
        try
        {
            var analysis = gray;
            if (roi is not null)
            {
                using var mask = roi.BuildMask(gray.Size());
                masked = new Mat(gray.Rows, gray.Cols, gray.Type(), Scalar.All(0));
                gray.CopyTo(masked, mask);
                analysis = masked;
            }

            JLVisionOperators.FindContours(analysis, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            var candidates = new List<VisionDetection>();
            foreach (var contour in contours)
            {
                var area = JLVisionOperators.ContourArea(contour);
                if (area < minArea) continue;
                if (maxArea > 0 && area > maxArea) continue;
                var perimeter = JLVisionOperators.ArcLength(contour, closed: true);
                var circularity = perimeter > 0 ? Math.Min(1, 4 * Math.PI * area / (perimeter * perimeter)) : 0;
                if (circularity < minCircularity) continue;

                JLVisionOperators.MinEnclosingCircle(contour, out Point2f center, out float radius);
                var rect = JLVisionOperators.BoundingRect(contour);
                candidates.Add(new VisionDetection(
                    Index: 0,
                    X: rect.X,
                    Y: rect.Y,
                    Width: rect.Width,
                    Height: rect.Height,
                    Area: area,
                    Perimeter: perimeter,
                    Circularity: circularity,
                    Circle: new VisionCircle(center.X, center.Y, radius),
                    BoundingBox: new VisionRectangle2D(rect.X, rect.Y, rect.Width, rect.Height)));
            }

            IEnumerable<VisionDetection> ordered = sortBy switch
            {
                "x" or "lefttoright" => descending
                    ? candidates.OrderByDescending(x => x.X).ThenByDescending(x => x.Y)
                    : candidates.OrderBy(x => x.X).ThenBy(x => x.Y),
                "y" or "toptobottom" => descending
                    ? candidates.OrderByDescending(x => x.Y).ThenByDescending(x => x.X)
                    : candidates.OrderBy(x => x.Y).ThenBy(x => x.X),
                _ => descending
                    ? candidates.OrderByDescending(x => x.Area).ThenBy(x => x.X)
                    : candidates.OrderBy(x => x.Area).ThenBy(x => x.X)
            };

            var detections = ordered
                .Take(maxCount)
                .Select((detection, index) => detection with { Index = index })
                .ToArray();

            var outputs = new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(sourceImage),
                ["count"] = VisionValue.Integer(detections.Length),
                ["detections"] = VisionValue.Detections(detections)
            };
            var overlays = new List<VisionOverlay>();
            for (var i = 0; i < detections.Length; i++)
            {
                var detection = detections[i];
                overlays.Add(new VisionOverlay(
                    $"blob-{i}",
                    VisionOverlayType.Rectangle,
                    Label: $"#{i + 1} A={detection.Area:0}",
                    X: detection.X,
                    Y: detection.Y,
                    Width: detection.Width,
                    Height: detection.Height,
                    Stroke: "#faad14",
                    StrokeWidth: 1.5));
                if (largestOnly && i == 0)
                {
                    overlays.Add(new VisionOverlay("blob-largest-circle", VisionOverlayType.Circle,
                        X: detection.Circle.X, Y: detection.Circle.Y, Radius: detection.Circle.Radius,
                        Stroke: "#52c41a", StrokeWidth: 2));
                    overlays.Add(new VisionOverlay("blob-largest-center", VisionOverlayType.Point,
                        X: detection.Circle.X, Y: detection.Circle.Y, Stroke: "#ff4d4f", StrokeWidth: 2));
                }
            }

            var summary = new Dictionary<string, object?>
            {
                ["mode"] = mode,
                ["count"] = detections.Length,
                ["totalArea"] = detections.Sum(x => x.Area),
                ["minArea"] = minArea,
                ["maxArea"] = maxArea <= 0 ? "unlimited" : maxArea,
                ["sortBy"] = sortBy,
                ["autoGray"] = converted,
                ["roi"] = roi?.Type.ToString() ?? "FullImage"
            };
            if (detections.Length > 0)
            {
                var best = detections[0];
                outputs["x"] = VisionValue.Double(best.Circle.X);
                outputs["y"] = VisionValue.Double(best.Circle.Y);
                outputs["area"] = VisionValue.Double(best.Area);
                outputs["radius"] = VisionValue.Double(best.Circle.Radius);
                outputs["center"] = VisionValue.Point(new VisionPoint2D(best.Circle.X, best.Circle.Y));
                outputs["circle"] = VisionValue.Circle(best.Circle);
                summary["bestArea"] = best.Area;
            }

            return ValueTask.FromResult(new NodeExecutorResult(outputs, summary, overlays));
        }
        finally
        {
            masked?.Dispose();
            ownedGray?.Dispose();
        }
    }
}
