using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ImageViewer.Controls
{
    internal static class ImageViewerAnalysisGraphRenderer
    {
        private const double DefaultChartWidth = 256;
        private const double DefaultChartHeight = 140;

        public static void DrawHistogram(Canvas canvas, int[] histogram, int histogramBinCount)
        {
            canvas.Children.Clear();
            if (histogram.Length == 0)
            {
                return;
            }

            int max = 0;
            foreach (int value in histogram)
            {
                if (value > max)
                {
                    max = value;
                }
            }

            if (max == 0)
            {
                return;
            }

            (double width, double height) = GetChartSize(canvas);
            var polygon = new Polygon
            {
                Fill = new SolidColorBrush(Color.FromArgb(100, 200, 200, 200)),
                Stroke = Brushes.White,
                StrokeThickness = 1
            };

            polygon.Points.Add(new Point(0, height));
            for (int i = 0; i < histogramBinCount; i++)
            {
                double x = i * width / (histogramBinCount - 1.0);
                double y = height - ((double)histogram[i] / max * height);
                polygon.Points.Add(new Point(x, y));
            }

            polygon.Points.Add(new Point(width, height));
            canvas.Children.Add(polygon);
        }

        public static void DrawProfile(Canvas canvas, ImageViewerProfileOutput output)
        {
            canvas.Children.Clear();
            ushort[]? samples16 = output.Samples16;
            byte[] data = output.ProfileData;
            int sampleCount = samples16?.Length ?? data.Length;
            if (sampleCount == 0)
            {
                return;
            }

            (double width, double height) = GetChartSize(canvas);
            canvas.Children.Add(new Line { X1 = 0, Y1 = height / 2, X2 = width, Y2 = height / 2, Stroke = Brushes.Gray, StrokeThickness = 0.5, Opacity = 0.3 });
            canvas.Children.Add(new Line { X1 = 0, Y1 = height, X2 = width, Y2 = height, Stroke = Brushes.Gray, StrokeThickness = 1 });

            var polyline = new Polyline
            {
                Stroke = Brushes.Cyan,
                StrokeThickness = 1.5
            };

            double maximum = samples16?.Length > 0 ? output.MaximumValue : byte.MaxValue;
            for (int i = 0; i < sampleCount; i++)
            {
                double x = (double)i / (sampleCount > 1 ? sampleCount - 1 : 1) * width;
                double sample = samples16 != null ? samples16[i] : data[i];
                double y = height - (sample / maximum * height);
                polyline.Points.Add(new Point(x, y));
            }

            canvas.Children.Add(polyline);
        }

        private static (double Width, double Height) GetChartSize(Canvas canvas)
        {
            double width = canvas.ActualWidth > 0 ? canvas.ActualWidth : canvas.Width;
            double height = canvas.ActualHeight > 0 ? canvas.ActualHeight : canvas.Height;

            if (double.IsNaN(width))
            {
                width = DefaultChartWidth;
            }

            if (double.IsNaN(height))
            {
                height = DefaultChartHeight;
            }

            return (width, height);
        }
    }
}
