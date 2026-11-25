// 程序集归属：VisionStudio.Imaging（2026-11-25 从 Engine/Runtime 迁出）。
// 算法层需要「从执行上下文取图像」，这个扩展只依赖 NodeExecutionContext（Abstractions）
// 与 VisionImage（本程序集），因此与图像类型同层，不留在引擎运行时。
// namespace 取 VisionStudio.Engine 父级：VisionStudio.Engine.* 下所有节点文件按命名空间
// 逐层查找即可见，不需要给上百个文件补 using。

using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine;

public static class NodeExecutionContextImageExtensions
{
    public static VisionImage RequireImage(this NodeExecutionContext context, string port)
    {
        var image = context.Require<IVisionImage>(port);
        if (image is VisionImage engineImage) return engineImage;
        if (image.NativeImage is OpenCvSharp.Mat mat) return VisionImage.Borrow(mat);
        throw new InvalidOperationException($"Input '{port}' exposes native image type '{image.NativeImage?.GetType().FullName ?? "null"}'; built-in OpenCV nodes require OpenCvSharp.Mat.");
    }
}
