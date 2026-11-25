// 2026-11-25 从 Engine/Nodes/ImageTransformNodes.cs 迁入图像层。
// 它只是"建一个输出 Mat、异常时释放"的 11 行工厂，不依赖任何节点语义；
// 算法层（BinarizationOps）与节点层都要用，留在 Nodes 会形成反向依赖。
// namespace 取 VisionStudio.Engine：VisionStudio.Engine.Nodes 下的文件按命名空间逐层查找即可见。

using OpenCvSharp;

namespace VisionStudio.Engine;

internal static class ImageTransformOutput
{
    public static Mat Create(Action<Mat> transform)
    {
        var output = new Mat();
        try { transform(output); return output; }
        catch { output.Dispose(); throw; }
    }
}
