using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Runtime;

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
