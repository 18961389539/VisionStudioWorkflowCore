using System.Windows;
using System.Windows.Media;
using ImageViewer.Models;
using ImageViewer.Utils;

namespace ImageViewer.Rendering
{
    internal static class DualEdgeCaliperRenderHelper
    {
        public static void DrawDetectionMarkers(RoiRenderContext context, CaliperMeasureRoi caliper, Brush invalidBrush, Brush rejectedBrush)
        {
            SingleEdgeCaliperRenderHelper.DrawEdgeMarkers(context, caliper.InvalidCaliperMarkers, invalidBrush, opacity: 0.45);
            SingleEdgeCaliperRenderHelper.DrawEdgeMarkers(context, caliper.RejectedEdge1Markers, rejectedBrush, opacity: 0.55);
            SingleEdgeCaliperRenderHelper.DrawEdgeMarkers(context, caliper.RejectedEdge2Markers, rejectedBrush, opacity: 0.55);
            SingleEdgeCaliperRenderHelper.DrawEdgeMarkers(context, caliper.Edge1Markers, Brushes.Cyan, opacity: 0.6);
            SingleEdgeCaliperRenderHelper.DrawEdgeMarkers(context, caliper.Edge2Markers, Brushes.Orange, opacity: 0.6);
            SingleEdgeCaliperRenderHelper.DrawScoreOverlays(context, caliper.ScoreOverlays);
        }

        public static void DrawLegend(RoiRenderContext context, CaliperMeasureRoi caliper, Brush measurementBrush, Brush invalidBrush, Brush rejectedBrush)
        {
            if (!caliper.HasDetectedEdges)
            {
                return;
            }

            Point anchor = new(caliper.CaliperCenter.X + 10 / context.Scale, caliper.CaliperCenter.Y - 28 / context.Scale);
            DrawLegendItem(context, anchor, measurementBrush, "手动参考线");
            DrawLegendItem(context, new Point(anchor.X, anchor.Y + 12 / context.Scale), Brushes.LimeGreen, "最终拟合线");
            if (context.ShowCaliperScores)
            {
                SingleEdgeCaliperRenderHelper.DrawLegend(context, new Point(anchor.X, anchor.Y + 24 / context.Scale), invalidBrush, rejectedBrush, showDiagnostics: true);
            }
        }

        public static string BuildSummaryText(RoiRenderContext context, CaliperMeasureRoi caliper)
        {
            string geometryText = $"宽度：{context.Measure(caliper).FormatPrimary()}";
            return SingleEdgeCaliperRenderHelper.BuildSummaryText(caliper, geometryText, "宽度测量", context.ShowCaliperScores);
        }

        /// <summary>
        /// Returns the anchor for the dual-edge summary label.
        /// The detected endpoints can move away from the original ROI center, so the label must follow
        /// the measured segment once detection has completed.
        /// </summary>
        public static Point GetSummaryAnchor(CaliperMeasureRoi caliper)
        {
            ArgumentNullException.ThrowIfNull(caliper);

            return GetMeasuredSegmentMidpoint(caliper);
        }

        /// <summary>
        /// Returns a summary anchor far enough from the measured segment to keep the complete label clear.
        /// A horizontal measurement places the label above the segment; a vertical measurement places it to a side.
        /// </summary>
        public static Point GetSummaryAnchor(RoiRenderContext context, CaliperMeasureRoi caliper)
            => GetSummaryAnchor(context, caliper, summaryText: null);

        public static Point GetSummaryAnchor(RoiRenderContext context, CaliperMeasureRoi caliper, string? summaryText)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(caliper);

            Point midpoint = GetMeasuredSegmentMidpoint(caliper);
            if (!caliper.HasDetectedEdges)
            {
                return midpoint;
            }

            Vector direction = caliper.P2.ToWpfPoint() - caliper.P1.ToWpfPoint();
            if (direction.LengthSquared < 1e-6)
            {
                return midpoint;
            }

            direction.Normalize();
            Vector normal = new(-direction.Y, direction.X);
            double screenOffset = Math.Max(12, context.InfoTextOffset);
            if (!string.IsNullOrWhiteSpace(summaryText))
            {
                Size labelSize = RoiRenderContext.MeasureInfoText(summaryText);
                Vector screenNormal = context.ToScreenPoint(midpoint + normal) - context.ToScreenPoint(midpoint);
                if (screenNormal.LengthSquared > 1e-6)
                {
                    screenNormal.Normalize();
                    double halfExtent = Math.Abs(screenNormal.X) * labelSize.Width / 2
                        + Math.Abs(screenNormal.Y) * labelSize.Height / 2;
                    screenOffset = Math.Max(screenOffset, halfExtent + 6);
                }
            }

            double offset = screenOffset / Math.Max(context.Scale, 1e-6);
            Point first = midpoint + normal * offset;
            Point second = midpoint - normal * offset;
            return context.ToScreenPoint(first).Y <= context.ToScreenPoint(second).Y ? first : second;
        }

        private static void DrawLegendItem(RoiRenderContext context, Point start, Brush brush, string text)
        {
            context.DrawLineSegment(start, new Point(start.X + 10 / context.Scale, start.Y), brush, 2 / context.Scale);
            context.DrawInfoText(text, new Point(start.X + 14 / context.Scale, start.Y - 5 / context.Scale), Brushes.White);
        }

        private static Point GetMeasuredSegmentMidpoint(CaliperMeasureRoi caliper)
        {
            (PointD p1, PointD p2) = GetMeasuredSegmentPoints(caliper);
            if (caliper.HasDetectedEdges)
            {
                return new Point((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2);
            }

            return caliper.CaliperCenter.ToWpfPoint();
        }

        private static (PointD P1, PointD P2) GetMeasuredSegmentPoints(CaliperMeasureRoi caliper)
        {
            // Older in-memory ROIs may only set HasDetectedEdges and P1/P2.
            // Fall back to the reference segment when no separate result segment exists.
            return (caliper.MeasurementP1, caliper.MeasurementP2);
        }
    }
}
