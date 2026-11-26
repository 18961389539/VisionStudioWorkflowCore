using System;
using System.Buffers;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Core.Analysis;
using ImageViewer.Models;

namespace ImageViewer.Services;

/// <summary>
/// WPF edge adapter for the managed analysis algorithms. It converts <see cref="BitmapSource"/>
/// pixels and WPF <see cref="Point"/>/<see cref="Vector"/> values into the framework-neutral
/// Core contracts and maps the results back for the rendering and ROI layer.
/// </summary>
internal static class VisionAlgorithmEntryPoint
{
    /// <summary>每行带最多拷贝这么多字节，避免大图转换时再额外持有一整幅字节缓冲。</summary>
    private const int ConversionBandBytes = 4 * 1024 * 1024;

    public static IntensityImage CreateIntensity(BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        return CreateIntensity(bitmap, new Int32Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
    }

    /// <summary>
    /// Converts one bitmap region into an intensity plane. Interactive probes only need a small
    /// neighborhood, so they avoid building a full-image plane for every pointer move.
    /// </summary>
    public static IntensityImage CreateIntensity(BitmapSource bitmap, Int32Rect region)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        BitmapSource source = ImageViewerPixelAccess.NormalizeForIntensity(bitmap);
        if (region.Width <= 0 || region.Height <= 0 ||
            region.X < 0 || region.Y < 0 ||
            region.X + region.Width > source.PixelWidth ||
            region.Y + region.Height > source.PixelHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(region), region, "The region must lie inside the bitmap.");
        }

        return CreateIntensityCore(source, region);
    }

    private static IntensityImage CreateIntensityCore(BitmapSource source, Int32Rect region)
    {
        int width = region.Width;
        int height = region.Height;
        PixelFormat format = source.Format;
        int stride = checked(((width * format.BitsPerPixel) + 31) / 32 * 4);
        var pixels = GC.AllocateUninitializedArray<ushort>(checked(width * height));

        int bandRows = Math.Max(1, Math.Min(height, ConversionBandBytes / Math.Max(1, stride)));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(checked(stride * bandRows));
        try
        {
            int copiedRows = 0;
            while (copiedRows < height)
            {
                int rows = Math.Min(bandRows, height - copiedRows);
                source.CopyPixels(
                    new Int32Rect(region.X, region.Y + copiedRows, width, rows),
                    buffer,
                    stride,
                    0);
                int outputOffset = copiedRows * width;
                if (format == PixelFormats.Gray8)
                {
                    ConvertGray8(buffer, pixels, outputOffset, width, rows, stride);
                }
                else if (format == PixelFormats.Gray16)
                {
                    ConvertGray16(buffer, pixels, outputOffset, width, rows, stride);
                }
                else if (format == PixelFormats.Bgr24)
                {
                    ConvertBgr24(buffer, pixels, outputOffset, width, rows, stride);
                }
                else
                {
                    ConvertBgra32(buffer, pixels, outputOffset, width, rows, stride, format);
                }

                copiedRows += rows;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new IntensityImage(width, height, ImageViewerPixelAccess.GetIntensityMaximum(format), pixels);
    }

    private static void ConvertGray8(byte[] buffer, ushort[] pixels, int outputOffset, int width, int rows, int stride)
    {
        for (int y = 0; y < rows; y++)
        {
            int sourceRow = y * stride;
            int outputRow = outputOffset + (y * width);
            for (int x = 0; x < width; x++)
            {
                pixels[outputRow + x] = buffer[sourceRow + x];
            }
        }
    }

    private static void ConvertGray16(byte[] buffer, ushort[] pixels, int outputOffset, int width, int rows, int stride)
    {
        for (int y = 0; y < rows; y++)
        {
            int sourceRow = y * stride;
            int outputRow = outputOffset + (y * width);
            for (int x = 0; x < width; x++)
            {
                int offset = sourceRow + (x * 2);
                pixels[outputRow + x] = (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
            }
        }
    }

    private static void ConvertBgr24(byte[] buffer, ushort[] pixels, int outputOffset, int width, int rows, int stride)
    {
        for (int y = 0; y < rows; y++)
        {
            int sourceRow = y * stride;
            int outputRow = outputOffset + (y * width);
            for (int x = 0; x < width; x++)
            {
                int offset = sourceRow + (x * 3);
                pixels[outputRow + x] = Luminance(buffer[offset], buffer[offset + 1], buffer[offset + 2]);
            }
        }
    }

    private static void ConvertBgra32(byte[] buffer, ushort[] pixels, int outputOffset, int width, int rows, int stride, PixelFormat format)
    {
        if (format != PixelFormats.Bgr32 && format != PixelFormats.Bgra32)
        {
            int bytesPerPixel = Math.Max(1, (format.BitsPerPixel + 7) / 8);
            for (int y = 0; y < rows; y++)
            {
                int sourceRow = y * stride;
                int outputRow = outputOffset + (y * width);
                for (int x = 0; x < width; x++)
                {
                    pixels[outputRow + x] = ImageViewerPixelAccess.ReadIntensity(
                        buffer, sourceRow + (x * bytesPerPixel), bytesPerPixel, format);
                }
            }

            return;
        }

        for (int y = 0; y < rows; y++)
        {
            int sourceRow = y * stride;
            int outputRow = outputOffset + (y * width);
            for (int x = 0; x < width; x++)
            {
                int offset = sourceRow + (x * 4);
                pixels[outputRow + x] = Luminance(buffer[offset], buffer[offset + 1], buffer[offset + 2]);
            }
        }
    }

    private static ushort Luminance(byte b, byte g, byte r)
    {
        return (ushort)Math.Clamp((int)Math.Round(0.299 * r + 0.587 * g + 0.114 * b), 0, byte.MaxValue);
    }

    public static bool TryFitLine(
        Point[] points,
        LineFitMode fitMode,
        int clippingEndPoints,
        out LineSegmentOverlay segment)
    {
        ArgumentNullException.ThrowIfNull(points);
        segment = default;
        if (!RobustFitAlgorithm.TryFitLine(ToPointD(points), fitMode, clippingEndPoints, out DetectedLineSegment fitted))
        {
            return false;
        }

        segment = new LineSegmentOverlay(fitted.Start, fitted.End);
        return true;
    }

    public static bool TryFitCircle(
        Point[] points,
        LineFitMode fitMode,
        out Point center,
        out double radius)
    {
        ArgumentNullException.ThrowIfNull(points);
        center = default;
        radius = 0;
        if (!RobustFitAlgorithm.TryFitCircle(ToPointD(points), fitMode, out PointD detectedCenter, out double detectedRadius))
        {
            return false;
        }

        center = new Point(detectedCenter.X, detectedCenter.Y);
        radius = detectedRadius;
        return true;
    }

    public static bool TryMeasureSingle(
        IntensityImage image,
        Point center,
        Vector measurementDirection,
        int searchRange,
        int averagingHalfWidth,
        double sigma,
        double minimumGradient,
        CaliperEdgePolarity polarity,
        int edgeSelection,
        out CaliperEdge sample) =>
        CaliperAlgorithm.TryMeasureSingle(
            image,
            new PointD(center.X, center.Y),
            new VectorD(measurementDirection.X, measurementDirection.Y),
            searchRange,
            averagingHalfWidth,
            sigma,
            minimumGradient,
            polarity,
            edgeSelection,
            out sample);

    public static bool TryMeasurePair(
        IntensityImage image,
        Point center,
        Vector measurementDirection,
        int searchRange,
        int averagingHalfWidth,
        double sigma,
        double minimumGradient,
        CaliperEdgePolarity polarity,
        double minimumEdgeGap,
        double nominalEdgeGap,
        double nominalEdgeGapTolerance,
        out CaliperEdgePair pair) =>
        CaliperAlgorithm.TryMeasurePair(
            image,
            new PointD(center.X, center.Y),
            new VectorD(measurementDirection.X, measurementDirection.Y),
            searchRange,
            averagingHalfWidth,
            sigma,
            minimumGradient,
            polarity,
            minimumEdgeGap,
            nominalEdgeGap,
            nominalEdgeGapTolerance,
            out pair);

    /// <summary>
    /// Measures one radial edge sample using a rectangular caliper oriented along the
    /// radial direction at the nominal radius.
    /// </summary>
    public static bool TryMeasureCircular(
        IntensityImage image,
        Point center,
        double nominalRadius,
        double angleRadians,
        double angleExtent,
        int searchRange,
        int averagingHalfWidth,
        double sigma,
        double minimumGradient,
        CaliperEdgePolarity polarity,
        int edgeSelection,
        out CaliperEdge sample) =>
        CaliperAlgorithm.TryMeasureCircular(
            image,
            new PointD(center.X, center.Y),
            nominalRadius,
            angleRadians,
            angleExtent,
            searchRange,
            averagingHalfWidth,
            sigma,
            minimumGradient,
            polarity,
            edgeSelection,
            out sample);

    /// <summary>全图斑点分割与连通域分析。</summary>
    public static IReadOnlyList<BlobFeature> ExtractBlobs(
        IntensityImage image,
        bool useOtsu,
        int threshold,
        bool detectDark,
        int minArea,
        bool includeContours = false,
        int maximumContourVertices = 512) =>
        BlobAlgorithm.ExtractRegions(
            image, inside: null, useOtsu, threshold, detectDark, minArea, includeContours, maximumContourVertices);

    /// <summary>
    /// 限定搜索掩码的斑点分割。<paramref name="inside"/> 之外的像素既不进入 Otsu 直方图，
    /// 也不会形成连通域；传 null 等价于全图搜索。
    /// </summary>
    public static IReadOnlyList<BlobFeature> ExtractBlobs(
        IntensityImage image,
        bool[]? inside,
        bool useOtsu,
        int threshold,
        bool detectDark,
        int minArea,
        bool includeContours = false,
        int maximumContourVertices = 512) =>
        BlobAlgorithm.ExtractRegions(
            image, inside, useOtsu, threshold, detectDark, minArea, includeContours, maximumContourVertices);

    private static PointD[] ToPointD(Point[] points)
    {
        var converted = new PointD[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            converted[i] = new PointD(points[i].X, points[i].Y);
        }

        return converted;
    }
}
