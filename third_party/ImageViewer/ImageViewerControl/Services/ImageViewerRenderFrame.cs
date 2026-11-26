using System.Windows.Media;

namespace ImageViewer.Services
{
    public sealed record ImageViewerRenderFrame(ImageSource? Source, double Left, double Top, double Width, double Height, double ScaleFactor, bool IsTiled);
}
