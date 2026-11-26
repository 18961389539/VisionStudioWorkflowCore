using System.Collections.ObjectModel;
using ImageViewer.Models;

namespace ImageViewer.ViewModels
{
    public partial class ImageViewerViewModel
    {
        // Compatibility projections over the generic ROI collection store.
        public ObservableCollection<RotatedRect> RectRois => GetRoiCollection<RotatedRect>();
        public ObservableCollection<EllipseRoi> EllipseRois => GetRoiCollection<EllipseRoi>();
        public ObservableCollection<FittedEllipseRoi> FittedEllipseRois => GetRoiCollection<FittedEllipseRoi>();
        public ObservableCollection<CircleRoi> CircleRois => GetRoiCollection<CircleRoi>();
        public ObservableCollection<RingRoi> RingRois => GetRoiCollection<RingRoi>();
        public ObservableCollection<CircularCaliperMeasureRoi> CircularCaliperMeasureRois => GetRoiCollection<CircularCaliperMeasureRoi>();
        public ObservableCollection<ArcCaliperMeasureRoi> ArcCaliperMeasureRois => GetRoiCollection<ArcCaliperMeasureRoi>();
        public ObservableCollection<LineCaliperMeasureRoi> LineCaliperMeasureRois => GetRoiCollection<LineCaliperMeasureRoi>();
        public ObservableCollection<BlobAnalysisRoi> BlobAnalysisRois => GetRoiCollection<BlobAnalysisRoi>();
        public ObservableCollection<PolygonRoi> PolygonRois => GetRoiCollection<PolygonRoi>();
        public ObservableCollection<PolylineRoi> PolylineRois => GetRoiCollection<PolylineRoi>();
        public ObservableCollection<PointAnnotationRoi> PointAnnotationRois => GetRoiCollection<PointAnnotationRoi>();
        public ObservableCollection<PointCoordinateMeasureRoi> PointCoordinateMeasureRois => GetRoiCollection<PointCoordinateMeasureRoi>();
        public ObservableCollection<TextAnnotationRoi> TextAnnotationRois => GetRoiCollection<TextAnnotationRoi>();
        public ObservableCollection<ArrowAnnotationRoi> ArrowAnnotationRois => GetRoiCollection<ArrowAnnotationRoi>();
        public ObservableCollection<LineMeasureRoi> LineMeasureRois => GetRoiCollection<LineMeasureRoi>();
        public ObservableCollection<CaliperMeasureRoi> CaliperMeasureRois => GetRoiCollection<CaliperMeasureRoi>();
        public ObservableCollection<AngleMeasureRoi> AngleMeasureRois => GetRoiCollection<AngleMeasureRoi>();
        public ObservableCollection<ArcMeasureRoi> ArcMeasureRois => GetRoiCollection<ArcMeasureRoi>();
        public ObservableCollection<PointToLineDistanceRoi> PointToLineDistanceRois => GetRoiCollection<PointToLineDistanceRoi>();
        public ObservableCollection<PointToCircleDistanceRoi> PointToCircleDistanceRois => GetRoiCollection<PointToCircleDistanceRoi>();
        public ObservableCollection<ParallelismMeasureRoi> ParallelismMeasureRois => GetRoiCollection<ParallelismMeasureRoi>();
        public ObservableCollection<PerpendicularityMeasureRoi> PerpendicularityMeasureRois => GetRoiCollection<PerpendicularityMeasureRoi>();
        public ObservableCollection<ConcentricityMeasureRoi> ConcentricityMeasureRois => GetRoiCollection<ConcentricityMeasureRoi>();
    }
}
