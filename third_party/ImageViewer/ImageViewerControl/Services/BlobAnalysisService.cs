using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using ImageViewer.Core.Analysis;
using ImageViewer.Models;

namespace ImageViewer.Services
{
    internal static class BlobAnalysisService
    {
        /// <summary>
        /// 轴对齐搜索区的便捷重载，等价于零角度旋转矩形。
        /// Chinese: 体数据分割等调用方本来就传入整块轴对齐区域，无需关心旋转几何。
        /// </summary>
        public static List<BlobFeature> DetectBlobs(
            BitmapSource bitmap,
            Rect searchRoi,
            bool useOtsu,
            int threshold,
            bool detectDark = false,
            int minArea = 10,
            CancellationToken cancellationToken = default)
        {
            if (searchRoi.Width <= 0 || searchRoi.Height <= 0)
            {
                return [];
            }

            var rotated = new RotatedRect
            {
                Center = new PointD(searchRoi.X + (searchRoi.Width / 2), searchRoi.Y + (searchRoi.Height / 2)),
                Width = searchRoi.Width,
                Height = searchRoi.Height,
                Angle = 0
            };

            return DetectBlobs(bitmap, rotated, useOtsu, threshold, detectDark, minArea, cancellationToken);
        }

        /// <summary>
        /// 使用托管算法完成斑点分割、连通域分析和几何特征提取。
        /// Chinese: 搜索区按 <paramref name="searchRoi"/> 的旋转内点收窄，斜框外的像素既不参与
        /// Otsu 阈值统计，也不会形成斑点；零角度时退化为整块矩形裁剪。
        /// English: The search region follows the rotated rectangle's interior, so pixels outside a
        /// slanted ROI neither enter the Otsu histogram nor form blobs.
        /// </summary>
        public static List<BlobFeature> DetectBlobs(
            BitmapSource bitmap,
            RotatedRect searchRoi,
            bool useOtsu,
            int threshold,
            bool detectDark = false,
            int minArea = 10,
            CancellationToken cancellationToken = default)
        {
            var results = new List<BlobFeature>();
            if (bitmap is null || searchRoi is null ||
                !double.IsFinite(searchRoi.Center.X) || !double.IsFinite(searchRoi.Center.Y) ||
                !double.IsFinite(searchRoi.Width) || !double.IsFinite(searchRoi.Height) ||
                !double.IsFinite(searchRoi.Angle) ||
                searchRoi.Width <= 0 || searchRoi.Height <= 0)
            {
                return results;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // The rotated rectangle's axis-aligned bounding box decides the crop; the mask then
            // narrows it back to the rotated interior.
            double radians = searchRoi.Angle * Math.PI / 180;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);
            double halfWidth = ((Math.Abs(cos) * searchRoi.Width) + (Math.Abs(sin) * searchRoi.Height)) / 2;
            double halfHeight = ((Math.Abs(sin) * searchRoi.Width) + (Math.Abs(cos) * searchRoi.Height)) / 2;
            int x = (int)Math.Clamp(Math.Floor(searchRoi.Center.X - halfWidth), 0, bitmap.PixelWidth);
            int y = (int)Math.Clamp(Math.Floor(searchRoi.Center.Y - halfHeight), 0, bitmap.PixelHeight);
            int right = (int)Math.Clamp(Math.Ceiling(searchRoi.Center.X + halfWidth), 0, bitmap.PixelWidth);
            int bottom = (int)Math.Clamp(Math.Ceiling(searchRoi.Center.Y + halfHeight), 0, bitmap.PixelHeight);
            int width = right - x;
            int height = bottom - y;
            if (width <= 0 || height <= 0)
            {
                return results;
            }

            // At zero angle the bounding box is the search region itself, so skip the full mask.
            bool[]? inside = searchRoi.Angle == 0
                ? null
                : CreateInsideMask(searchRoi, x, y, width, height, cos, sin, cancellationToken);

            BitmapSource normalized = ImageViewerPixelAccess.NormalizeForIntensity(bitmap);
            var crop = new CroppedBitmap(normalized, new Int32Rect(x, y, width, height));
            if (crop.CanFreeze)
            {
                crop.Freeze();
            }

            IntensityImage image = VisionAlgorithmEntryPoint.CreateIntensity(crop);
            cancellationToken.ThrowIfCancellationRequested();

            int areaThreshold = Math.Max(1, minArea);
            IReadOnlyList<BlobFeature> regions = VisionAlgorithmEntryPoint.ExtractBlobs(
                image, inside, useOtsu, threshold, detectDark, areaThreshold, includeContours: true);

            for (int i = 0; i < regions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BlobFeature region = regions[i];
                results.Add(region with
                {
                    // Rendering shows "#<Label>", so keep the dense 1-based numbering the
                    // control has always produced rather than the raw component label.
                    Label = i + 1,
                    Centroid = new PointD(region.Centroid.X + x, region.Centroid.Y + y),
                    BoundingBox = new RectD(
                        region.BoundingBox.X + x,
                        region.BoundingBox.Y + y,
                        region.BoundingBox.Width,
                        region.BoundingBox.Height),
                    Contour = OffsetContour(region.Contour, x, y)
                });
            }

            return results;
        }

        /// <summary>
        /// Moves a crop-relative contour back into image coordinates.
        /// Chinese: 轮廓点是裁剪区内的格点，必须加回裁剪左上角偏移才能与图像对齐。
        /// </summary>
        private static PointD[]? OffsetContour(PointD[]? contour, int offsetX, int offsetY)
        {
            if (contour is null || contour.Length == 0)
            {
                return contour;
            }

            var shifted = new PointD[contour.Length];
            for (int i = 0; i < contour.Length; i++)
            {
                shifted[i] = new PointD(contour[i].X + offsetX, contour[i].Y + offsetY);
            }

            return shifted;
        }

        /// <summary>
        /// Builds a row-major mask of the pixels whose centers fall inside the rotated rectangle.
        /// Chinese: 与 <see cref="ImageContourExtractionService"/>、参数推荐采样使用同一内点判定公式。
        /// </summary>
        private static bool[] CreateInsideMask(
            RotatedRect searchRoi,
            int left,
            int top,
            int width,
            int height,
            double cos,
            double sin,
            CancellationToken cancellationToken)
        {
            var inside = new bool[checked(width * height)];
            double halfWidth = searchRoi.Width / 2;
            double halfHeight = searchRoi.Height / 2;
            for (int row = 0; row < height; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double dy = top + row + 0.5 - searchRoi.Center.Y;
                int rowOffset = row * width;
                for (int column = 0; column < width; column++)
                {
                    double dx = left + column + 0.5 - searchRoi.Center.X;
                    double localX = (dx * cos) + (dy * sin);
                    double localY = (dy * cos) - (dx * sin);
                    inside[rowOffset + column] = Math.Abs(localX) < halfWidth && Math.Abs(localY) < halfHeight;
                }
            }

            return inside;
        }
    }
}
