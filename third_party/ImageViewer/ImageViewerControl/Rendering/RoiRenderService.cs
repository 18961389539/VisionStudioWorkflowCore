using System;
using System.Collections.Generic;
using System.Windows.Media;
using ImageViewer.Models;
using ImageViewer.Plugins;

namespace ImageViewer.Rendering
{
    public sealed partial class RoiRenderService
    {
        private readonly RoiPluginRegistry _pluginRegistry;

        public RoiRenderService(RoiPluginRegistry pluginRegistry)
        {
            _pluginRegistry = pluginRegistry ?? throw new ArgumentNullException(nameof(pluginRegistry));
        }

        internal static IReadOnlyDictionary<Type, IRoiRenderer> CreateBuiltInRendererMap()
        {
            return new Dictionary<Type, IRoiRenderer>
            {
                [typeof(RotatedRect)] = new RotatedRectRenderer(),
                [typeof(EllipseRoi)] = new EllipseRoiRenderer(),
                [typeof(FittedEllipseRoi)] = new FittedEllipseRoiRenderer(),
                [typeof(CircleRoi)] = new CircleRoiRenderer(),
                [typeof(RingRoi)] = new RingRoiRenderer(),
                [typeof(CircularCaliperMeasureRoi)] = new CircularCaliperMeasureRenderer(),
                [typeof(ArcCaliperMeasureRoi)] = new ArcCaliperMeasureRenderer(),
                [typeof(PolygonRoi)] = new PolygonRoiRenderer(),
                [typeof(BlobAnalysisRoi)] = new BlobAnalysisRenderer(),
                [typeof(PolylineRoi)] = new PolylineRoiRenderer(),
                [typeof(PointAnnotationRoi)] = new PointAnnotationRenderer(),
                [typeof(PointCoordinateMeasureRoi)] = new PointCoordinateMeasureRenderer(),
                [typeof(TextAnnotationRoi)] = new TextAnnotationRenderer(),
                [typeof(ArrowAnnotationRoi)] = new ArrowAnnotationRenderer(),
                [typeof(LineMeasureRoi)] = new LineMeasureRenderer(),
                [typeof(LineCaliperMeasureRoi)] = new LineCaliperMeasureRenderer(),
                [typeof(CaliperMeasureRoi)] = new CaliperMeasureRenderer(),
                [typeof(AngleMeasureRoi)] = new AngleMeasureRenderer(),
                [typeof(ArcMeasureRoi)] = new ArcMeasureRenderer(),
                [typeof(PointToLineDistanceRoi)] = new PointToLineDistanceRenderer(),
                [typeof(PointToCircleDistanceRoi)] = new PointToCircleDistanceRenderer(),
                [typeof(ParallelismMeasureRoi)] = new ParallelismMeasureRenderer(),
                [typeof(PerpendicularityMeasureRoi)] = new PerpendicularityMeasureRenderer(),
                [typeof(ConcentricityMeasureRoi)] = new ConcentricityMeasureRenderer(),
                [typeof(CenterDistanceMeasureRoi)] = new CenterDistanceMeasureRenderer(),
                [typeof(ThreePointCircleMeasureRoi)] = new ThreePointCircleMeasureRenderer()
            };
        }

        public void RenderCommitted(IEnumerable<RoiBase> rois, RoiRenderContext context, RoiBase? selectedRoi)
        {
            foreach (var roi in rois)
            {
                if (ReferenceEquals(roi, selectedRoi))
                {
                    continue;
                }

                Render(roi, context, null, false);
            }
        }

        public void RenderSelected(RoiBase? roi, RoiRenderContext context)
        {
            if (roi == null)
            {
                return;
            }

            Render(roi, context, Brushes.Red, true);
        }

        public void RenderActive(IEnumerable<RoiBase> rois, RoiRenderContext context)
        {
            foreach (var roi in rois)
            {
                Render(roi, context, Brushes.Orange, false);
            }
        }

        public void Render(RoiBase roi, RoiRenderContext context, Brush? strokeOverride, bool isSelected)
        {
            var plugin = _pluginRegistry.FindByRoi(roi)
                ?? throw new InvalidOperationException($"No ROI plugin registered for type '{roi.GetType().FullName}'.");

            plugin.Renderer.Render(roi, context, strokeOverride, isSelected);
        }
    }
}
