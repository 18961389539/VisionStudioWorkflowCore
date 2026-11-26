using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using ImageViewer.Drawing;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Services;
using ImageViewer.Utils;

namespace ImageViewer.Plugins
{
    /// <summary>
    /// 内置 ROI 工具的绘制控制器声明。
    /// Chinese: 全部内置工具的绘制行为集中在此声明，新增可绘制类型只需在此加一条 + 注册插件，
    /// 无需改动 ImageViewer 主控件。
    /// English: Declarative home for the built-in ROI draw controllers. Adding a new drawable type only
    /// requires one entry here plus a plugin registration - the ImageViewer control stays untouched.
    /// </summary>
    internal static class BuiltInDrawControllers
    {
        public static IRoiDrawController RotatedRect { get; } = new RoiDrawController(
            Cursors.Cross,
            static () => new DragDrawSession<RotatedRect>(
                createInitial: static (_, start) => new RotatedRect { Center = start.ToPointD(), Width = 0, Height = 0 },
                applyDrag: static (_, roi, origin, current) =>
                {
                    roi.Width = Math.Abs(current.X - origin.X);
                    roi.Height = Math.Abs(current.Y - origin.Y);
                    roi.Center = MidPoint(origin, current).ToPointD();
                },
                shouldCommit: static (host, roi) => roi.Width > host.MinimumDrawableSize && roi.Height > host.MinimumDrawableSize));

        public static IRoiDrawController BlobAnalysis { get; } = new RoiDrawController(
            Cursors.Cross,
            static () => new DragDrawSession<BlobAnalysisRoi>(
                createInitial: static (_, start) => new BlobAnalysisRoi { Center = start.ToPointD(), Width = 0, Height = 0 },
                applyDrag: static (_, roi, origin, current) =>
                {
                    roi.Width = Math.Abs(current.X - origin.X);
                    roi.Height = Math.Abs(current.Y - origin.Y);
                    roi.Center = MidPoint(origin, current).ToPointD();
                },
                shouldCommit: static (host, roi) => roi.Width > host.MinimumDrawableSize && roi.Height > host.MinimumDrawableSize,
                beforeCommit: static (host, roi) => host.TryApplyAnalysis(roi)));

        public static IRoiDrawController Ellipse { get; } = new RoiDrawController(
            Cursors.Cross,
            static () => new DragDrawSession<EllipseRoi>(
                createInitial: static (_, start) => new EllipseRoi { Center = start.ToPointD(), RadiusX = 0, RadiusY = 0 },
                applyDrag: static (_, roi, origin, current) =>
                {
                    roi.RadiusX = Math.Abs(current.X - origin.X) / 2;
                    roi.RadiusY = Math.Abs(current.Y - origin.Y) / 2;
                    roi.Center = MidPoint(origin, current).ToPointD();
                },
                shouldCommit: static (host, roi) => roi.RadiusX > host.MinimumDrawableSize && roi.RadiusY > host.MinimumDrawableSize));

        public static IRoiDrawController Circle { get; } = new RoiDrawController(
            Cursors.Cross,
            static () => new DragDrawSession<CircleRoi>(
                createInitial: static (_, start) => new CircleRoi { Center = start.ToPointD(), Radius = 0 },
                applyDrag: static (_, roi, origin, current) => roi.Radius = GeometryUtils.Distance(origin, current),
                shouldCommit: static (host, roi) => roi.Radius > host.MinimumDrawableSize));

        /// <summary>
        /// 圆环：单段拖拽 + 确认点击。
        /// Chinese: 按下圆心拖出外径，拖动过程中内径始终为外径的一半（预览即最终形状）；
        /// 第一次抬起不提交，只进入确认等待，再点一次左键才提交，右键随时取消。
        /// English: Ring, drawn in a single drag plus a confirmation click. Dragging fixes the outer
        /// radius and keeps the inner radius at half of it, so the preview already shows the final
        /// proportions. The first release only enters a confirmation wait; the next left click commits.
        /// </summary>
        public static IRoiDrawController Ring { get; } = new RoiDrawController(
            Cursors.Cross,
            static () => new DragDrawSession<RingRoi>(
                createInitial: static (_, start) => new RingRoi { Center = start.ToPointD(), InnerRadius = 0, OuterRadius = 0 },
                applyDrag: static (_, roi, origin, current) =>
                {
                    roi.OuterRadius = GeometryUtils.Distance(origin, current);
                    roi.InnerRadius = roi.OuterRadius / 2;
                },
                shouldCommit: static (host, roi) => RoiGeometryService.IsRingDrawable(roi.OuterRadius, host.MinimumDrawableSize),
                beforeCommit: static (host, roi) => roi.InnerRadius = RoiGeometryService.ClampRingInnerRadius(
                    roi.InnerRadius,
                    roi.OuterRadius,
                    host.MinimumDrawableSize),
                finalStage: 1,
                shouldAdvanceStage: static (host, roi) => RoiGeometryService.IsRingDrawable(roi.OuterRadius, host.MinimumDrawableSize),
                cancelOnRightButton: true));

        public static IRoiDrawController CircularCaliper { get; } = new RoiDrawController(
            Cursors.Cross,
            static () => new DragDrawSession<CircularCaliperMeasureRoi>(
                createInitial: static (_, start) => new CircularCaliperMeasureRoi { Center = start.ToPointD(), Radius = 0 },
                applyDrag: static (host, roi, origin, current) =>
                {
                    roi.Center = origin.ToPointD();
                    roi.Radius = GeometryUtils.Distance(origin, current);

                    if (roi.Radius > host.MinimumLineLength)
                    {
                        host.TryApplyAnalysis(roi);
                    }
                    else
                    {
                        roi.ClearDetectedEdges();
                    }
                },
                shouldCommit: static (host, roi) => roi.Radius > host.MinimumDrawableSize,
                beforeCommit: static (host, roi) => host.TryApplyAnalysis(roi)));

        public static IRoiDrawController AutomaticCircle { get; } = new RoiDrawController(
            Cursors.Cross,
            static () => new ClickPlaceDrawSession<CircularCaliperMeasureRoi>(
                static (host, position) =>
                {
                    return host.TryCreateAutomaticCircle(position, out CircularCaliperMeasureRoi roi)
                        ? roi
                        : null;
                },
                useSnappedPosition: false,
                handlesEvent: true));

        public static IRoiDrawController ArcCaliper { get; } = new RoiDrawController(
            Cursors.Cross,
            static () => new DragDrawSession<ArcCaliperMeasureRoi>(
                createInitial: static (_, start) => new ArcCaliperMeasureRoi { Center = start.ToPointD(), Radius = 0 },
                applyDrag: static (host, roi, origin, current) =>
                {
                    roi.Center = origin.ToPointD();
                    roi.Radius = GeometryUtils.Distance(origin, current);

                    if (roi.Radius > host.MinimumLineLength)
                    {
                        host.TryApplyAnalysis(roi);
                    }
                    else
                    {
                        roi.ClearDetectedEdges();
                    }
                },
                shouldCommit: static (host, roi) => roi.Radius > host.MinimumDrawableSize,
                beforeCommit: static (host, roi) => host.TryApplyAnalysis(roi)));

        public static IRoiDrawController LineMeasure { get; } = new RoiDrawController(
            Cursors.Pen,
            static () => new DragDrawSession<LineMeasureRoi>(
                createInitial: static (_, start) => new LineMeasureRoi { P1 = start.ToPointD(), P2 = start.ToPointD() },
                applyDrag: static (_, roi, origin, current) => roi.P2 = current.ToPointD(),
                shouldCommit: static (host, roi) => GeometryUtils.Distance(roi.P1.ToWpfPoint(), roi.P2.ToWpfPoint()) > host.MinimumDrawableSize));

        public static IRoiDrawController ArrowAnnotation { get; } = new RoiDrawController(
            Cursors.Pen,
            static () => new DragDrawSession<ArrowAnnotationRoi>(
                createInitial: static (_, start) => new ArrowAnnotationRoi { P1 = start.ToPointD(), P2 = start.ToPointD() },
                applyDrag: static (_, roi, origin, current) => roi.P2 = current.ToPointD(),
                shouldCommit: static (host, roi) => GeometryUtils.Distance(roi.P1.ToWpfPoint(), roi.P2.ToWpfPoint()) > host.MinimumDrawableSize));

        public static IRoiDrawController CaliperMeasure { get; } = new RoiDrawController(
            Cursors.Pen,
            static () => new DragDrawSession<CaliperMeasureRoi>(
                createInitial: static (_, start) =>
                {
                    var roi = new CaliperMeasureRoi { P1 = start.ToPointD(), P2 = start.ToPointD() };
                    roi.SyncCaliperRegionFromMeasurementLine(updateSearchRange: false);
                    return roi;
                },
                applyDrag: static (host, roi, origin, current) =>
                {
                    roi.P2 = current.ToPointD();
                    roi.SyncCaliperRegionFromMeasurementLine();

                    if (GeometryUtils.Distance(roi.P1.ToWpfPoint(), roi.P2.ToWpfPoint()) > host.MinimumLineLength)
                    {
                        host.TryApplyAnalysis(roi);
                    }
                    else
                    {
                        roi.ClearDetectedEdges();
                    }
                },
                shouldCommit: static (host, roi) => GeometryUtils.Distance(roi.P1.ToWpfPoint(), roi.P2.ToWpfPoint()) > host.MinimumDrawableSize,
                beforeCommit: static (host, roi) => host.TryApplyAnalysis(roi)));

        public static IRoiDrawController LineCaliper { get; } = new RoiDrawController(
            Cursors.Pen,
            static () => new DragDrawSession<LineCaliperMeasureRoi>(
                createInitial: static (_, start) => new LineCaliperMeasureRoi { P1 = start.ToPointD(), P2 = start.ToPointD() },
                applyDrag: static (host, roi, origin, current) =>
                {
                    var preview = (LineCaliperMeasureRoi)roi.Clone();
                    preview.P1 = origin.ToPointD();
                    preview.P2 = current.ToPointD();

                    if (GeometryUtils.Distance(preview.P1.ToWpfPoint(), preview.P2.ToWpfPoint()) > host.MinimumLineLength && host.TryApplyAnalysis(preview))
                    {
                        roi.ApplyFrom(preview);
                    }
                    else
                    {
                        roi.P1 = origin.ToPointD();
                        roi.P2 = current.ToPointD();
                        roi.ClearDetectedLine();
                    }
                },
                shouldCommit: static (host, roi) => GeometryUtils.Distance(roi.P1.ToWpfPoint(), roi.P2.ToWpfPoint()) > host.MinimumDrawableSize));

        public static IRoiDrawController AngleMeasure { get; } = new RoiDrawController(
            Cursors.Pen,
            static () => new StepClickDrawSession<AngleMeasureRoi>(
                finalStep: 2,
                createInitial: static (_, position, _) => new AngleMeasureRoi { P1 = position.ToPointD(), Vertex = position.ToPointD(), P2 = position.ToPointD() },
                tryComplete: static (roi, position, _) =>
                {
                    roi.P2 = position.ToPointD();
                    return true;
                },
                updateStep: static (roi, step, position, _) =>
                {
                    if (step == 1)
                    {
                        roi.Vertex = position.ToPointD();
                        roi.P2 = position.ToPointD();
                    }
                },
                updateActivePreview: static (_, roi, step, position, _) =>
                {
                    if (step == 1)
                    {
                        roi.Vertex = position.ToPointD();
                        roi.P2 = position.ToPointD();
                    }
                    else if (step == 2)
                    {
                        roi.P2 = position.ToPointD();
                    }
                }));

        public static IRoiDrawController ArcMeasure { get; } = new RoiDrawController(
            Cursors.Pen,
            static () => new StepClickDrawSession<ArcMeasureRoi>(
                finalStep: 2,
                createInitial: static (_, position, _) => new ArcMeasureRoi { StartPoint = position.ToPointD(), EndPoint = position.ToPointD(), ArcPoint = position.ToPointD() },
                tryComplete: static (roi, position, _) =>
                {
                    roi.ArcPoint = position.ToPointD();
                    return true;
                },
                updateStep: static (roi, step, position, _) =>
                {
                    if (step == 1)
                    {
                        roi.EndPoint = position.ToPointD();
                        roi.ArcPoint = position.ToPointD();
                    }
                },
                updateActivePreview: static (_, roi, step, position, _) =>
                {
                    if (step == 1)
                    {
                        roi.EndPoint = position.ToPointD();
                        roi.ArcPoint = position.ToPointD();
                    }
                    else if (step == 2)
                    {
                        roi.ArcPoint = position.ToPointD();
                    }
                }));

        public static IRoiDrawController ThreePointCircle { get; } = new RoiDrawController(
            Cursors.Pen,
            static () => new StepClickDrawSession<ThreePointCircleMeasureRoi>(
                finalStep: 2,
                createInitial: static (_, position, _) => new ThreePointCircleMeasureRoi
                {
                    P1 = position.ToPointD(),
                    P2 = position.ToPointD(),
                    P3 = position.ToPointD()
                },
                tryComplete: static (roi, position, _) =>
                {
                    roi.P3 = position.ToPointD();
                    return roi.IsValid;
                },
                updateStep: static (roi, step, position, _) =>
                {
                    if (step == 1)
                    {
                        roi.P2 = position.ToPointD();
                        roi.P3 = position.ToPointD();
                    }
                },
                updateActivePreview: static (_, roi, step, position, _) =>
                {
                    if (step == 1)
                    {
                        roi.P2 = position.ToPointD();
                        roi.P3 = position.ToPointD();
                    }
                    else if (step == 2)
                    {
                        roi.P3 = position.ToPointD();
                    }
                }));

        public static IRoiDrawController PointToLineDistance { get; } = new RoiDrawController(
            Cursors.Pen,
            static () => new StepClickDrawSession<PointToLineDistanceRoi>(
                finalStep: 1,
                createInitial: static (_, position, hitRoi) =>
                {
                    Point anchor = ResolvePoint(hitRoi, position);
                    return new PointToLineDistanceRoi { Point = anchor.ToPointD(), LineP1 = anchor.ToPointD(), LineP2 = anchor.ToPointD() };
                },
                tryComplete: static (roi, _, hitRoi) =>
                {
                    if (!TryResolveLine(hitRoi, out Point p1, out Point p2))
                    {
                        return false;
                    }

                    roi.LineP1 = p1.ToPointD();
                    roi.LineP2 = p2.ToPointD();
                    return true;
                },
                updateIdleCursor: static (host, hitRoi) => ApplySelectionCursor(host, TryResolveLine(hitRoi, out _, out _)),
                updateActivePreview: static (host, roi, step, _, hitRoi) =>
                {
                    if (step != 1)
                    {
                        return;
                    }

                    if (TryResolveLine(hitRoi, out Point p1, out Point p2))
                    {
                        roi.LineP1 = p1.ToPointD();
                        roi.LineP2 = p2.ToPointD();
                        ApplySelectionCursor(host, true);
                        return;
                    }

                    roi.LineP1 = roi.Point;
                    roi.LineP2 = roi.Point;
                    ApplySelectionCursor(host, false);
                }));

        public static IRoiDrawController PointToCircleDistance { get; } = new RoiDrawController(
            Cursors.Pen,
            static () => new StepClickDrawSession<PointToCircleDistanceRoi>(
                finalStep: 1,
                createInitial: static (_, position, hitRoi) =>
                {
                    Point anchor = ResolvePoint(hitRoi, position);
                    return new PointToCircleDistanceRoi { Point = anchor.ToPointD(), Center = anchor.ToPointD(), Radius = 1 };
                },
                tryComplete: static (roi, _, hitRoi) =>
                {
                    if (!TryResolveCircle(hitRoi, out Point center, out double radius))
                    {
                        return false;
                    }

                    roi.Center = center.ToPointD();
                    roi.Radius = radius;
                    return true;
                },
                updateIdleCursor: static (host, hitRoi) => ApplySelectionCursor(host, TryResolveCircle(hitRoi, out _, out _)),
                updateActivePreview: static (host, roi, step, _, hitRoi) =>
                {
                    if (step != 1)
                    {
                        return;
                    }

                    if (TryResolveCircle(hitRoi, out Point center, out double radius))
                    {
                        roi.Center = center.ToPointD();
                        roi.Radius = radius;
                        ApplySelectionCursor(host, true);
                        return;
                    }

                    roi.Center = roi.Point;
                    roi.Radius = 1;
                    ApplySelectionCursor(host, false);
                }));

        public static IRoiDrawController Parallelism { get; } = new RoiDrawController(
            Cursors.Pen,
            static () => new StepClickDrawSession<ParallelismMeasureRoi>(
                finalStep: 2,
                createInitial: static (_, _, hitRoi) => TryResolveLine(hitRoi, out Point p1, out Point p2)
                    ? new ParallelismMeasureRoi { Line1P1 = p1.ToPointD(), Line1P2 = p2.ToPointD(), Line2P1 = p1.ToPointD(), Line2P2 = p2.ToPointD() }
                    : null,
                tryComplete: static (roi, _, hitRoi) =>
                {
                    if (!TryResolveLine(hitRoi, out Point p1, out Point p2))
                    {
                        return false;
                    }

                    roi.Line2P1 = p1.ToPointD();
                    roi.Line2P2 = p2.ToPointD();
                    return true;
                },
                updateStep: static (roi, step, _, hitRoi) => ApplyPairedLineStep(roi, step, hitRoi),
                updateIdleCursor: static (host, hitRoi) => ApplySelectionCursor(host, TryResolveLine(hitRoi, out _, out _)),
                updateActivePreview: static (host, roi, step, _, hitRoi) => ApplyPairedLinePreview(host, roi, step, hitRoi)));

        public static IRoiDrawController Perpendicularity { get; } = new RoiDrawController(
            Cursors.Pen,
            static () => new StepClickDrawSession<PerpendicularityMeasureRoi>(
                finalStep: 2,
                createInitial: static (_, _, hitRoi) => TryResolveLine(hitRoi, out Point p1, out Point p2)
                    ? new PerpendicularityMeasureRoi { Line1P1 = p1.ToPointD(), Line1P2 = p2.ToPointD(), Line2P1 = p1.ToPointD(), Line2P2 = p2.ToPointD() }
                    : null,
                tryComplete: static (roi, _, hitRoi) =>
                {
                    if (!TryResolveLine(hitRoi, out Point p1, out Point p2))
                    {
                        return false;
                    }

                    roi.Line2P1 = p1.ToPointD();
                    roi.Line2P2 = p2.ToPointD();
                    return true;
                },
                updateStep: static (roi, step, _, hitRoi) => ApplyPairedLineStep(roi, step, hitRoi),
                updateIdleCursor: static (host, hitRoi) => ApplySelectionCursor(host, TryResolveLine(hitRoi, out _, out _)),
                updateActivePreview: static (host, roi, step, _, hitRoi) => ApplyPairedLinePreview(host, roi, step, hitRoi)));

        public static IRoiDrawController Concentricity { get; } = new RoiDrawController(
            Cursors.Pen,
            static () => new StepClickDrawSession<ConcentricityMeasureRoi>(
                finalStep: 2,
                createInitial: static (_, _, hitRoi) => TryResolveCircle(hitRoi, out Point center, out double radius)
                    ? new ConcentricityMeasureRoi { Center1 = center.ToPointD(), Radius1 = radius, Center2 = center.ToPointD(), Radius2 = radius }
                    : null,
                tryComplete: static (roi, _, hitRoi) =>
                {
                    if (!TryResolveCircle(hitRoi, out Point center, out double radius))
                    {
                        return false;
                    }

                    roi.Center2 = center.ToPointD();
                    roi.Radius2 = radius;
                    return true;
                },
                updateStep: static (roi, step, _, hitRoi) => ApplyConcentricityStep(roi, step, hitRoi),
                updateIdleCursor: static (host, hitRoi) => ApplySelectionCursor(host, TryResolveCircle(hitRoi, out _, out _)),
                updateActivePreview: static (host, roi, step, _, hitRoi) => ApplyConcentricityPreview(host, roi, step, hitRoi)));

        public static IRoiDrawController CenterDistance { get; } = new RoiDrawController(
            Cursors.Pen,
            static () => new StepClickDrawSession<CenterDistanceMeasureRoi>(
                finalStep: 1,
                createInitial: static (_, _, hitRoi) => TryResolveCircle(hitRoi, out Point center, out _)
                    ? new CenterDistanceMeasureRoi { Center1 = center.ToPointD(), Center2 = center.ToPointD() }
                    : null,
                tryComplete: static (roi, _, hitRoi) =>
                {
                    if (!TryResolveCircle(hitRoi, out Point center, out double radius))
                    {
                        return false;
                    }

                    roi.Center2 = center.ToPointD();
                    return true;
                },
                updateStep: static (roi, _, _, hitRoi) =>
                {
                    if (TryResolveCircle(hitRoi, out Point center, out double radius))
                    {
                        roi.Center2 = center.ToPointD();
                    }
                },
                updateIdleCursor: static (host, hitRoi) => ApplySelectionCursor(host, TryResolveCircle(hitRoi, out _, out _)),
                updateActivePreview: static (host, roi, _, _, hitRoi) =>
                {
                    if (TryResolveCircle(hitRoi, out Point center, out double radius))
                    {
                        roi.Center2 = center.ToPointD();
                        ApplySelectionCursor(host, true);
                    }
                    else
                    {
                        ApplySelectionCursor(host, false);
                    }
                }));

        public static IRoiDrawController Polygon { get; } = new RoiDrawController(
            Cursors.Pen,
            static () => new PathDrawSession<PolygonRoi>(
                new PolygonRoi(),
                static roi => new PointDBridgeCollection(roi.Points),
                new PathDrawOptions
                {
                    MinimumCommitPoints = 3,
                    CommitOnModeExit = true,
                    CloseOnCommit = true,
                    ShowClosingPreview = true,
                    ShowPointCountInfo = true
                },
                beforeCommit: static (_, roi) => roi.IsClosed = true));

        public static IRoiDrawController Polyline { get; } = new RoiDrawController(
            Cursors.Pen,
            static () => new PathDrawSession<PolylineRoi>(
                new PolylineRoi(),
                static roi => new PointDBridgeCollection(roi.Points),
                new PathDrawOptions { MinimumCommitPoints = 2 }));

        public static IRoiDrawController FreehandPolyline { get; } = new RoiDrawController(
            Cursors.Pen,
            static () => new PathDrawSession<PolylineRoi>(
                new PolylineRoi { IsFreehand = true },
                static roi => new PointDBridgeCollection(roi.Points),
                new PathDrawOptions
                {
                    MinimumCommitPoints = 2,
                    FinishOnRightButton = false,
                    FinishOnMouseUp = true,
                    AppendPointsWhileDragging = true,
                    ClearPointsOnFirstDown = true
                }));

        public static IRoiDrawController FittedEllipse { get; } = new RoiDrawController(
            Cursors.UpArrow,
            static () => new ClickPlaceDrawSession<FittedEllipseRoi>(
                static (host, position) =>
                {
                    RoiBase? source = host.HitTest(position);
                    return source == null ? null : CreateFittedEllipseFromSource(source);
                }));

        public static IRoiDrawController PointAnnotation { get; } = new RoiDrawController(
            Cursors.Cross,
            static () => new ClickPlaceDrawSession<PointAnnotationRoi>(
                static (_, position) => new PointAnnotationRoi
                {
                    Position = position.ToPointD(),
                    Label = $"P ({position.X:F0},{position.Y:F0})"
                }));

        public static IRoiDrawController PointCoordinate { get; } = new RoiDrawController(
            Cursors.Cross,
            static () => new ClickPlaceDrawSession<PointCoordinateMeasureRoi>(
                static (_, position) => new PointCoordinateMeasureRoi
                {
                    Position = position.ToPointD()
                },
                useSnappedPosition: true,
                handlesEvent: true));

        public static IRoiDrawController AutomaticEdgePoint { get; } = new RoiDrawController(
            Cursors.Cross,
            static () => new EdgeSnapDrawSession(
                static (position, score, confidence) => new PointCoordinateMeasureRoi
                {
                    Position = position.ToPointD(),
                    EdgeScore = score,
                    EdgeConfidence = confidence,
                    IsEdgeSnapped = true
                }));

        public static IRoiDrawController TextAnnotation { get; } = new RoiDrawController(
            Cursors.IBeam,
            static () => new ClickPlaceDrawSession<TextAnnotationRoi>(
                static (host, position) =>
                {
                    string? text = host.RequestTextInput(
                        UiText.Get("DialogTextAnnotationPrompt"),
                        UiText.Get("DialogTextAnnotationDefault"));

                    return string.IsNullOrWhiteSpace(text)
                        ? null
                        : new TextAnnotationRoi { Position = position.ToPointD(), Label = text.Trim() };
                }));

        private static FittedEllipseRoi? CreateFittedEllipseFromSource(RoiBase source)
        {
            if (!TryGetEllipseFitSourcePoints(source, out IReadOnlyList<Point>? points) || points == null)
            {
                return null;
            }

            if (!GeometryUtils.TryFitEllipse(points, EllipseFitOptions.Default, out EllipseFitResult fit))
            {
                return null;
            }

            return new FittedEllipseRoi
            {
                Center = fit.Center.ToPointD(),
                RadiusX = fit.RadiusX,
                RadiusY = fit.RadiusY,
                Angle = fit.AngleDegrees,
                SourcePointCount = points.Count,
                FitResidualRms = fit.ResidualRms,
                FitResidualMedian = fit.ResidualMedian,
                FitResidualMax = fit.ResidualMax,
                FitNoiseScale = fit.NoiseScale,
                FitInlierCount = fit.InlierCount,
                FitOutlierCount = fit.OutlierCount,
                FitAspectRatio = fit.AspectRatio,
                FitAlgorithm = fit.Algorithm,
                Label = string.IsNullOrWhiteSpace(source.Label) ? "Fit" : $"Fit {source.Label}"
            };
        }

        private static bool TryGetEllipseFitSourcePoints(RoiBase source, out IReadOnlyList<Point>? points)
        {
            points = source switch
            {
                PolygonRoi polygon when polygon.Points.Count >= 3 => polygon.Points.ToWpfPointArray(),
                PolylineRoi polyline when polyline.Points.Count >= 3 => polyline.Points.ToWpfPointArray(),
                _ => null
            };

            return points != null;
        }

        private static void ApplyPairedLineStep<TRoi>(TRoi roi, int step, RoiBase? hitRoi)
            where TRoi : RoiBase
        {
            if (step != 1 || !TryResolveLine(hitRoi, out Point p1, out Point p2))
            {
                return;
            }

            ApplyPairedLine(roi, p1, p2);
        }

        private static void ApplyPairedLinePreview<TRoi>(IRoiDrawHost host, TRoi roi, int step, RoiBase? hitRoi)
            where TRoi : RoiBase
        {
            if (TryResolveLine(hitRoi, out Point p1, out Point p2))
            {
                if (step == 1)
                {
                    ApplyPairedLine(roi, p1, p2);
                    ApplySelectionCursor(host, true);
                }
                else if (step == 2)
                {
                    ApplySecondaryLine(roi, p1, p2);
                    ApplySelectionCursor(host, true);
                }

                return;
            }

            if (step == 2)
            {
                ApplySecondaryLine(roi, GetPrimaryLineStart(roi), GetPrimaryLineEnd(roi));
                ApplySelectionCursor(host, false);
            }
        }

        private static void ApplyConcentricityStep(ConcentricityMeasureRoi roi, int step, RoiBase? hitRoi)
        {
            if (step != 1 || !TryResolveCircle(hitRoi, out Point center, out double radius))
            {
                return;
            }

            ApplyPairedCircle(roi, center, radius);
        }

        private static void ApplyConcentricityPreview(IRoiDrawHost host, ConcentricityMeasureRoi roi, int step, RoiBase? hitRoi)
        {
            if (TryResolveCircle(hitRoi, out Point center, out double radius))
            {
                if (step == 1)
                {
                    ApplyPairedCircle(roi, center, radius);
                    ApplySelectionCursor(host, true);
                }
                else if (step == 2)
                {
                    roi.Center2 = center.ToPointD();
                    roi.Radius2 = radius;
                    ApplySelectionCursor(host, true);
                }

                return;
            }

            if (step == 2)
            {
                roi.Center2 = roi.Center1;
                roi.Radius2 = roi.Radius1;
                ApplySelectionCursor(host, false);
            }
        }

        private static void ApplyPairedLine<TRoi>(TRoi roi, Point p1, Point p2)
            where TRoi : RoiBase
        {
            ApplyPrimaryLine(roi, p1, p2);
            ApplySecondaryLine(roi, p1, p2);
        }

        private static void ApplyPrimaryLine<TRoi>(TRoi roi, Point p1, Point p2)
            where TRoi : RoiBase
        {
            switch (roi)
            {
                case ParallelismMeasureRoi parallelism:
                    parallelism.Line1P1 = p1.ToPointD();
                    parallelism.Line1P2 = p2.ToPointD();
                    break;
                case PerpendicularityMeasureRoi perpendicularity:
                    perpendicularity.Line1P1 = p1.ToPointD();
                    perpendicularity.Line1P2 = p2.ToPointD();
                    break;
            }
        }

        private static void ApplySecondaryLine<TRoi>(TRoi roi, Point p1, Point p2)
            where TRoi : RoiBase
        {
            switch (roi)
            {
                case ParallelismMeasureRoi parallelism:
                    parallelism.Line2P1 = p1.ToPointD();
                    parallelism.Line2P2 = p2.ToPointD();
                    break;
                case PerpendicularityMeasureRoi perpendicularity:
                    perpendicularity.Line2P1 = p1.ToPointD();
                    perpendicularity.Line2P2 = p2.ToPointD();
                    break;
            }
        }

        private static Point GetPrimaryLineStart<TRoi>(TRoi roi)
            where TRoi : RoiBase
        {
            return roi switch
            {
                ParallelismMeasureRoi parallelism => parallelism.Line1P1.ToWpfPoint(),
                PerpendicularityMeasureRoi perpendicularity => perpendicularity.Line1P1.ToWpfPoint(),
                _ => default
            };
        }

        private static Point GetPrimaryLineEnd<TRoi>(TRoi roi)
            where TRoi : RoiBase
        {
            return roi switch
            {
                ParallelismMeasureRoi parallelism => parallelism.Line1P2.ToWpfPoint(),
                PerpendicularityMeasureRoi perpendicularity => perpendicularity.Line1P2.ToWpfPoint(),
                _ => default
            };
        }

        private static void ApplyPairedCircle(ConcentricityMeasureRoi roi, Point center, double radius)
        {
            roi.Center1 = center.ToPointD();
            roi.Radius1 = radius;
            roi.Center2 = center.ToPointD();
            roi.Radius2 = radius;
        }

        private static bool TryResolveLine(RoiBase? hitRoi, out Point p1, out Point p2)
        {
            if (hitRoi is LineMeasureRoi line)
            {
                p1 = line.P1.ToWpfPoint();
                p2 = line.P2.ToWpfPoint();
                return true;
            }

            p1 = default;
            p2 = default;
            return false;
        }

        private static bool TryResolveCircle(RoiBase? hitRoi, out Point center, out double radius)
        {
            if (hitRoi is CircleRoi circle)
            {
                center = circle.Center.ToWpfPoint();
                radius = circle.Radius;
                return true;
            }

            if (hitRoi is ThreePointCircleMeasureRoi threePointCircle && threePointCircle.IsValid)
            {
                center = threePointCircle.Center.ToWpfPoint();
                radius = threePointCircle.Radius;
                return true;
            }

            if (hitRoi is ArcMeasureRoi arc && arc.IsValid)
            {
                center = arc.Center.ToWpfPoint();
                radius = arc.Radius;
                return true;
            }

            center = default;
            radius = 0;
            return false;
        }

        private static Point ResolvePoint(RoiBase? hitRoi, Point fallback)
        {
            return hitRoi is PointAnnotationRoi annotation ? annotation.Position.ToWpfPoint() : fallback;
        }

        private static void ApplySelectionCursor(IRoiDrawHost host, bool hasSelection)
        {
            host.SetCursor(hasSelection ? Cursors.Hand : Cursors.Pen);
        }

        private static Point MidPoint(Point start, Point end)
        {
            return new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2);
        }

        /// <summary>
        /// 把模型的 ObservableCollection&lt;PointD&gt; 适配为路径绘制会话所需的 ObservableCollection&lt;Point&gt;。
        /// Chinese: 路径会话会直接增删集合元素，这里把变更即时回写模型，保持模型为唯一数据源。
        /// English: Adapts the model's ObservableCollection&lt;PointD&gt; into the ObservableCollection&lt;Point&gt;
        /// required by the path draw session; mutations are mirrored straight back to the model so the model
        /// remains the single source of truth.
        /// </summary>
        private sealed class PointDBridgeCollection : ObservableCollection<Point>
        {
            private readonly ObservableCollection<PointD> _target;

            public PointDBridgeCollection(ObservableCollection<PointD> target)
            {
                _target = target;
                foreach (PointD point in target)
                {
                    Items.Add(point.ToWpfPoint());
                }
            }

            protected override void InsertItem(int index, Point item)
            {
                base.InsertItem(index, item);
                _target.Insert(index, item.ToPointD());
            }

            protected override void SetItem(int index, Point item)
            {
                base.SetItem(index, item);
                _target[index] = item.ToPointD();
            }

            protected override void RemoveItem(int index)
            {
                base.RemoveItem(index);
                _target.RemoveAt(index);
            }

            protected override void ClearItems()
            {
                base.ClearItems();
                _target.Clear();
            }
        }
    }
}
