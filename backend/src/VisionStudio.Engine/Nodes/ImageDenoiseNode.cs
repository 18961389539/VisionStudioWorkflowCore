using System.Diagnostics;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 非局部均值降噪（image.denoise，2026-10-27）。
///
/// 现有 `image.blur.gaussian` / `image.blur.median` 只能做邻域平滑；对低照、高感、DPM 点阵
/// 这类"噪声颗粒大但边缘必须保留"的图像，高斯会同时糊掉边缘，中值只能压椒盐。OpenCV 5 的
/// fastNlMeansDenoising / fastNlMeansDenoisingColored 用图像块相似度降噪，适合作为测量前的
/// 可选预处理。节点只接受 8 位 1/3 通道输入（OpenCV NLM 的契约），16 位/浮点请先转换。
/// </summary>
public sealed class ImageDenoiseNode : IVisionNodeExecutor
{
    public string Type => "image.denoise";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var source = context.RequireImage("image");
        var sourceMat = source.Mat;
        if (sourceMat.Empty())
            throw new InvalidOperationException("image.denoise requires a non-empty image.");
        if (sourceMat.Depth() != MatType.CV_8U)
            throw new InvalidOperationException("image.denoise requires an 8-bit image; convert 16-bit or float sources first.");
        if (sourceMat.Channels() is not (1 or 3))
            throw new InvalidOperationException($"image.denoise supports 1 or 3 channel images; received {sourceMat.Channels()}.");

        var mode = node.GetString("mode", "auto").Trim().ToLowerInvariant();
        if (mode is not ("auto" or "gray" or "color"))
            throw new InvalidOperationException("image.denoise mode must be auto, gray or color.");
        if (mode == "gray" && sourceMat.Channels() != 1)
            throw new InvalidOperationException("image.denoise mode=gray requires a single-channel image; use mode=auto or color for BGR input.");
        if (mode == "color" && sourceMat.Channels() != 3)
            throw new InvalidOperationException("image.denoise mode=color requires a 3-channel BGR image; use mode=auto or gray for grayscale input.");

        var useColor = mode == "color" || (mode == "auto" && sourceMat.Channels() == 3);
        var h = node.GetDouble("h", 3);
        if (!double.IsFinite(h) || h is < 0 or > 100)
            throw new InvalidOperationException("image.denoise h must be finite and within 0..100.");
        var hColor = useColor ? node.GetDouble("hColor", 3) : 0;
        if (useColor && (!double.IsFinite(hColor) || hColor is < 0 or > 100))
            throw new InvalidOperationException("image.denoise hColor must be finite and within 0..100.");
        var templateWindowSize = node.GetInt("templateWindowSize", 7);
        if (templateWindowSize is < 3 or > 99 || templateWindowSize % 2 == 0)
            throw new InvalidOperationException("image.denoise templateWindowSize must be an odd integer within 3..99.");
        var searchWindowSize = node.GetInt("searchWindowSize", 21);
        if (searchWindowSize is < 3 or > 255 || searchWindowSize % 2 == 0)
            throw new InvalidOperationException("image.denoise searchWindowSize must be an odd integer within 3..255.");

        var roi = ReadRoi(context, node);
        using var mask = roi?.BuildMask(sourceMat.Size(), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        Mat? denoised = null;
        Mat? output = null;
        try
        {
            denoised = new Mat();
            if (useColor)
                Cv2.FastNlMeansDenoisingColored(sourceMat, denoised, (float)h, (float)hColor,
                    templateWindowSize, searchWindowSize);
            else
                Cv2.FastNlMeansDenoising(sourceMat, denoised, (float)h, templateWindowSize, searchWindowSize);
            cancellationToken.ThrowIfCancellationRequested();

            if (mask is null)
            {
                output = denoised;
                denoised = null;
            }
            else
            {
                // ROI 语义：只替换选区内的像素，选区外保持原图，避免把"降噪范围"误解成"裁剪范围"。
                using var inverse = new Mat();
                JLVisionOperators.BitwiseNot(mask, inverse);
                output = denoised.Clone();
                sourceMat.CopyTo(output, inverse);
            }

            var image = VisionImage.Own(output);
            output = null;
            timer.Stop();
            return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
            {
                var diagnostics = CoordinateValidation.Diagnostics(
                    Type,
                    ("mode", useColor ? "color" : "gray"),
                    ("h", h),
                    ("hColor", useColor ? hColor : null),
                    ("templateWindowSize", templateWindowSize),
                    ("searchWindowSize", searchWindowSize),
                    ("roiApplied", roi is not null),
                    ("width", image.Width),
                    ("height", image.Height),
                    ("channels", image.Mat.Channels()),
                    ("elapsedMs", timer.Elapsed.TotalMilliseconds));
                return new NodeExecutorResult(
                    new Dictionary<string, VisionValue>
                    {
                        ["image"] = VisionValue.Image(image),
                        ["width"] = VisionValue.Integer(image.Width),
                        ["height"] = VisionValue.Integer(image.Height),
                        ["channels"] = VisionValue.Integer(image.Mat.Channels()),
                        ["diagnostics"] = VisionValue.String(diagnostics)
                    },
                    new Dictionary<string, object?>
                    {
                        ["mode"] = useColor ? "color" : "gray",
                        ["h"] = h,
                        ["hColor"] = useColor ? hColor : null,
                        ["templateWindowSize"] = templateWindowSize,
                        ["searchWindowSize"] = searchWindowSize,
                        ["roiApplied"] = roi is not null,
                        ["width"] = image.Width,
                        ["height"] = image.Height,
                        ["channels"] = image.Mat.Channels(),
                        ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                        ["algorithmBackend"] = "opencv",
                        ["diagnostics"] = diagnostics
                    });
            }, cancellationToken));
        }
        finally
        {
            denoised?.Dispose();
            output?.Dispose();
        }
    }

    private static VisionRoi? ReadRoi(NodeExecutionContext context, NodeDefinition node) =>
        context.TryGetValue("roi", out var value) ? value.Require<VisionRoi>("roi") : node.GetRoi();
}
