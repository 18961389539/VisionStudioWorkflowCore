using System.Windows;
using System.Windows.Media.Imaging;

namespace ImageViewer.Services
{
    public sealed record ImageViewerAnalysisRequest(BitmapSource Bitmap, Point P1, Point P2);
}
