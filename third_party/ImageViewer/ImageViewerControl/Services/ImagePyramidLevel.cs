using System.Windows.Media.Imaging;

namespace ImageViewer.Services
{
    public sealed record ImagePyramidLevel(BitmapSource Bitmap, double ScaleFactor);
}
