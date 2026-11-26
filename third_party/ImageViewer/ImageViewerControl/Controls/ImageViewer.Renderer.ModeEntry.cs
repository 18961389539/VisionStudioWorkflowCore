using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using ImageViewer.Drawing;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Plugins;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Use StartLineMeasureMode() or StartCaliperMeasureMode() for explicit measure-mode entry.", false)]
        public void StartMeasureMode()
        {
            StartCaliperMeasureMode();
        }

        public void StartRoiMode()
        {
            StartDraw(BuiltInDrawControllers.RotatedRect, UiText.Get("ToolRotatedRect"));
        }

        public void StartBlobAnalysisMode()
        {
            StartDraw(BuiltInDrawControllers.BlobAnalysis, UiText.Get("ToolBlobAnalysis"));
        }

        public void StartCircleRoiMode()
        {
            StartDraw(BuiltInDrawControllers.Circle, UiText.Get("ToolCircle"));
        }

        public void StartRingRoiMode()
        {
            StartDraw(BuiltInDrawControllers.Ring, UiText.Get("ToolRing"));
        }

        public void StartCircularCaliperMeasureMode()
        {
            StartDraw(BuiltInDrawControllers.CircularCaliper, UiText.Get("ToolCircularCaliper"));
        }

        public void StartAutomaticCircleMeasureMode()
        {
            StartDraw(BuiltInDrawControllers.AutomaticCircle, UiText.Get("ToolAutomaticCircle"));
        }

        public void StartArcCaliperMeasureMode()
        {
            StartDraw(BuiltInDrawControllers.ArcCaliper, UiText.Get("ToolArcCaliper"));
        }

        public void StartLineCaliperMeasureMode()
        {
            StartDraw(BuiltInDrawControllers.LineCaliper, UiText.Get("ToolLineCaliper"));
        }

        public void StartPointAnnotationMode()
        {
            StartDraw(BuiltInDrawControllers.PointAnnotation, UiText.Get("ToolPointAnnotation"));
        }

        public void StartPointCoordinateMeasureMode()
        {
            StartDraw(BuiltInDrawControllers.PointCoordinate, UiText.Get("ToolPointCoordinate"));
        }

        public void StartAutomaticEdgePointMeasureMode()
        {
            StartDraw(BuiltInDrawControllers.AutomaticEdgePoint, UiText.Get("ToolAutomaticEdgePoint"));
        }

        public void StartArrowAnnotationMode()
        {
            StartDraw(BuiltInDrawControllers.ArrowAnnotation, UiText.Get("ToolArrowAnnotation"));
        }

        public void StartTextAnnotationMode()
        {
            StartDraw(BuiltInDrawControllers.TextAnnotation, UiText.Get("ToolTextAnnotation"));
        }

        public void StartPolylineRoiMode(bool freehand)
        {
            StartDraw(
                freehand ? BuiltInDrawControllers.FreehandPolyline : BuiltInDrawControllers.Polyline,
                UiText.Get(freehand ? "ToolFreehand" : "ToolPolyline"));
        }

        public void StartPolygonRoiMode()
        {
            StartDraw(BuiltInDrawControllers.Polygon, UiText.Get("ToolPolygon"));
        }

        public void StartAreaMeasureMode()
        {
            StartDraw(BuiltInDrawControllers.Polygon, UiText.Get("ToolAreaMeasure"));
        }

        public void StartPolylineMeasureMode()
        {
            StartDraw(BuiltInDrawControllers.Polyline, UiText.Get("ToolPolylineMeasure"));
        }

        /// <summary>
        /// 进入外部落点模式：每次点击由调用方提供的工厂创建 ROI。
        /// Chinese: 落点使用未吸附的原始坐标，与既有行为一致。
        /// English: Enters external placement mode: each click creates an ROI from the caller-supplied
        /// factory. The placement uses the raw (unsnapped) coordinate, matching the existing behavior.
        /// </summary>
        public void StartPlacementMode(Func<Point, RoiBase?> createRoi, Cursor? cursor = null)
        {
            ArgumentNullException.ThrowIfNull(createRoi);

            StartDraw(new RoiDrawController(
                cursor ?? Cursors.Cross,
                () => new ClickPlaceDrawSession<RoiBase>(
                    (_, position) => createRoi(position),
                    useSnappedPosition: false,
                    handlesEvent: true)));
        }

        public void StartFitEllipseMode()
        {
            StartDraw(BuiltInDrawControllers.FittedEllipse, UiText.Get("ToolFittedEllipse"));
        }

        public void StartLineMeasureMode()
        {
            StartDraw(BuiltInDrawControllers.LineMeasure, UiText.Get("ToolLineMeasure"));
        }

        public void StartCaliperMeasureMode()
        {
            StartDraw(BuiltInDrawControllers.CaliperMeasure, UiText.Get("ToolCaliperMeasure"));
        }

        public void StartAngleMeasureMode()
        {
            StartDraw(BuiltInDrawControllers.AngleMeasure, UiText.Get("ToolAngleMeasure"));
        }

        public void StartArcMeasureMode()
        {
            StartDraw(BuiltInDrawControllers.ArcMeasure, UiText.Get("ToolArcMeasure"));
        }

        public void StartThreePointCircleMeasureMode()
        {
            StartDraw(BuiltInDrawControllers.ThreePointCircle, UiText.Get("ToolThreePointCircle"));
        }

        public void StartPointToLineMeasureMode()
        {
            StartDraw(BuiltInDrawControllers.PointToLineDistance, UiText.Get("ToolPointToLineDistance"));
        }

        public void StartPointToCircleMeasureMode()
        {
            StartDraw(BuiltInDrawControllers.PointToCircleDistance, UiText.Get("ToolPointToCircleDistance"));
        }

        public void StartParallelismMeasureMode()
        {
            StartDraw(BuiltInDrawControllers.Parallelism, UiText.Get("ToolParallelism"));
        }

        public void StartPerpendicularityMeasureMode()
        {
            StartDraw(BuiltInDrawControllers.Perpendicularity, UiText.Get("ToolPerpendicularity"));
        }

        public void StartConcentricityMeasureMode()
        {
            StartDraw(BuiltInDrawControllers.Concentricity, UiText.Get("ToolConcentricity"));
        }

        public void StartCenterDistanceMeasureMode()
        {
            StartDraw(BuiltInDrawControllers.CenterDistance, UiText.Get("ToolCenterDistance"));
        }

    }
}
