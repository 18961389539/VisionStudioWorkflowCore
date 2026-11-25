// 2026-11-25 从 Engine/Nodes/ContourNodes.cs 的 ContourNodesHelper 抽到算法层。
//
// 为什么单独成类：模板匹配（JLVisionMatching.Orchestration / .Training）与轮廓族节点
// 都要把掩膜/模板二值化，语义完全一致（auto=Otsu、fixed=固定阈值、direct=非零即前景）。
// 此前它挂在 ContourNodesHelper 上，使算法实现为了一个 30 行函数反向依赖节点目录。

using OpenCvSharp;

namespace VisionStudio.Engine.Algorithms;

internal static class BinarizationOps
{
    /// <summary>
    /// 二值化：auto/otsu（前景=亮，invert 时取反）、fixed（固定阈值）、direct（非零像素即前景）。
    /// 返回调用方负责释放的 Mat；autoGray 记录是否发生了颜色→灰度转换（供 summary）。
    /// </summary>
    public static Mat Binarize(
        Mat source,
        string mode,
        double threshold,
        bool invert,
        out bool autoGray,
        out string effectiveMode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        mode = mode.Trim().ToLowerInvariant();
        if (mode is not ("auto" or "otsu" or "fixed" or "direct"))
            throw new InvalidOperationException("Contour binarization must be auto, fixed or direct.");
        if (mode == "fixed" && (!double.IsFinite(threshold) || threshold is < 0 or > 255))
            throw new InvalidOperationException("Fixed contour threshold must be finite in 0..255.");
        effectiveMode = mode is "auto" or "otsu" ? "otsu" : mode;
        var gray = PreprocessImageHelpers.ToGray8(source, out autoGray, out var owned);
        try
        {
            return ImageTransformOutput.Create(binary =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (mode is "auto" or "otsu")
                {
                    JLVisionOperators.Threshold(gray, binary, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
                    if (invert) JLVisionOperators.BitwiseNot(binary, binary);
                }
                else
                {
                    JLVisionOperators.Threshold(gray, binary, mode == "fixed" ? threshold : 0, 255,
                        invert ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary);
                }
                cancellationToken.ThrowIfCancellationRequested();
            });
        }
        finally
        {
            owned?.Dispose();
        }
    }
}
