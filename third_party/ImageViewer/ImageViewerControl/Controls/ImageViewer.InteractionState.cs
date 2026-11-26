using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ImageViewer.Models;
using ImageViewer.ViewModels;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        private const double PixelGridScaleThreshold = 10;
        private const double HandleSize = 8;
        private const double InfoTextOffset = 20;
        private const double AngleArcRadius = 30;
        private const double HitTestTolerance = 5;
        private const double MinimumDrawableSize = 0.1;
        private const double MinimumRoiDimension = 1;
        private const double PolygonVertexHitPadding = 4;
        private const double PolygonResizeHandlePadding = 2;
        private const double PolygonCloseHighlightPadding = 8;
        private const double HandleHitPadding = 6;
        private const double PointAnnotationSize = 6;
        private const double MinimumLineLength = 1.0;

        private BitmapSource? _cachedInfoBitmap;
        private RoiBase? _cachedInfoRoi;
        private double _cachedInfoPixelSize;
        private string? _cachedInfoUnit;
        private string _cachedInfoText = string.Empty;
        private Path? _pixelGridPath;
        private ImageSource? _cachedPixelGridImageSource;
        private double _cachedPixelGridWidth;
        private double _cachedPixelGridHeight;
        private Canvas ScreenOverlayCanvas => (Canvas)FindName("screenOverlayCanvas");

        private bool TryCaptureRootGridMouse()
        {
            return rootGrid.IsMouseCaptured || rootGrid.CaptureMouse();
        }

        private void ReleaseRootGridMouseIfCaptured()
        {
            if (rootGrid.IsMouseCaptured)
            {
                rootGrid.ReleaseMouseCapture();
            }
        }
    }
}
