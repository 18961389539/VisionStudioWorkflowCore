using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageViewer.Controls
{
    internal static class ImageViewerImageSourceUtilities
    {
        /// <summary>
        /// 计算可视区域对应的图像坐标矩形。
        /// Chinese: 网格线与 ROI 的可视区域裁剪共用这一份换算（平移取负除以缩放，再按图像尺寸截断），
        /// 并支持以屏幕像素为单位的余量（换算回图像单位），保证贴边的标注/手柄不被裁掉。
        /// English: Shared image-space viewport math used by grid lines and ROI culling, with an optional screen-pixel margin.
        /// </summary>
        public static Rect GetVisibleImageRect(Size imageSize, Size viewportSize, double scale, double translateX, double translateY, double marginPixels = 0)
        {
            if (!double.IsFinite(scale) || scale <= 0)
            {
                return new Rect(0, 0, imageSize.Width, imageSize.Height);
            }

            double margin = Math.Max(0, marginPixels) / Math.Max(scale, 0.1);
            double left = Math.Max(-margin, -translateX / scale - margin);
            double top = Math.Max(-margin, -translateY / scale - margin);
            double right = Math.Min(imageSize.Width + margin, (viewportSize.Width - translateX) / scale + margin);
            double bottom = Math.Min(imageSize.Height + margin, (viewportSize.Height - translateY) / scale + margin);
            if (right <= left || bottom <= top)
            {
                return Rect.Empty;
            }

            return new Rect(left, top, right - left, bottom - top);
        }

        public static bool TryGetSourceImageSize(ImageSource? source, out Size imageSize)
        {
            switch (source)
            {
                case BitmapSource { PixelWidth: > 0, PixelHeight: > 0 } bitmap:
                    imageSize = new Size(bitmap.PixelWidth, bitmap.PixelHeight);
                    return true;
                case ImageSource { Width: > 0, Height: > 0 } image:
                    imageSize = new Size(image.Width, image.Height);
                    return true;
                default:
                    imageSize = default;
                    return false;
            }
        }
    }
}