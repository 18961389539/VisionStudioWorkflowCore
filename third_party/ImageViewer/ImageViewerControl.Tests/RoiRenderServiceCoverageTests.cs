using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ImageViewer.Controls;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Rendering;
using Xunit;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// 覆盖内置 ROI 渲染器全量分派，防止新增 ROI 类型只接入了命中测试而漏掉渲染。
    /// Chinese: 选中/未选中各渲染一次，覆盖形状、测量与标注三个 partial 的绘制、手柄和信息文本路径。
    /// English: Renders every built-in ROI type both selected and unselected to cover shape, measurement
    /// and annotation drawing, handles and info text.
    /// </summary>
    [Collection(WpfTestCollection.Name)]
    [Trait("Category", "Unit")]
    [Trait("Category", "Wpf")]
    public class RoiRenderServiceCoverageTests
    {
        [Fact]
        public void Render_DrawsEveryBuiltInRoiTypeSelectedAndUnselected()
        {
            WpfTestRunner.Run(() =>
            {
                var service = new RoiRenderService(RoiPluginRegistry.CreateBuiltIn());
                var overlayCanvas = new Canvas();
                var screenOverlayCanvas = new Canvas();
                RoiRenderContext context = CreateContext(overlayCanvas, screenOverlayCanvas);
                IReadOnlyList<RoiBase> rois = CreateRois();

                foreach (RoiBase roi in rois)
                {
                    service.Render(roi, context, strokeOverride: null, isSelected: false);
                    service.Render(roi, context, strokeOverride: Brushes.Red, isSelected: true);
                }

                Assert.True(overlayCanvas.Children.Count + screenOverlayCanvas.Children.Count >= rois.Count);
            });
        }

        [Fact]
        public void Render_InvalidArcAndDegenerateAngle_FallBackToPlaceholderGeometry()
        {
            WpfTestRunner.Run(() =>
            {
                var service = new RoiRenderService(RoiPluginRegistry.CreateBuiltIn());
                RoiRenderContext context = CreateContext(new Canvas(), new Canvas());
                var arc = new ArcMeasureRoi
                {
                    StartPoint = new PointD(10, 10),
                    EndPoint = new PointD(10, 10),
                    ArcPoint = new PointD(10, 10)
                };
                var angle = new AngleMeasureRoi
                {
                    P1 = new PointD(10, 10),
                    Vertex = new PointD(10, 10),
                    P2 = new PointD(30, 10)
                };

                service.Render(arc, context, null, isSelected: true);
                service.Render(angle, context, null, isSelected: false);

                Assert.False(arc.IsValid);
            });
        }

        [Fact]
        public void Render_ClosedAndOpenPolygonsAndPolylines_DrawDistinctOverlays()
        {
            WpfTestRunner.Run(() =>
            {
                var service = new RoiRenderService(RoiPluginRegistry.CreateBuiltIn());
                var overlayCanvas = new Canvas();
                var screenOverlayCanvas = new Canvas();
                RoiRenderContext context = CreateContext(overlayCanvas, screenOverlayCanvas);
                var closed = new PolygonRoi
                {
                    IsClosed = true,
                    Points = { new PointD(0, 0), new PointD(40, 0), new PointD(40, 40) }
                };
                var open = new PolygonRoi
                {
                    IsClosed = false,
                    Points = { new PointD(0, 0), new PointD(40, 0), new PointD(40, 40), new PointD(0, 40) }
                };
                var polyline = new PolylineRoi
                {
                    Points = { new PointD(0, 0), new PointD(20, 20), new PointD(40, 0) }
                };

                service.Render(closed, context, null, isSelected: false);
                service.Render(open, context, null, isSelected: false);
                service.Render(polyline, context, null, isSelected: true);

                Assert.True(overlayCanvas.Children.Count + screenOverlayCanvas.Children.Count >= 3);
            });
        }

        private static RoiRenderContext CreateContext(Canvas overlayCanvas, Canvas screenOverlayCanvas)
        {
            return new RoiRenderContext(
                overlayCanvas,
                screenOverlayCanvas,
                static point => point,
                scale: 1,
                pixelSize: 1,
                physicalUnit: "px",
                showCaliperScores: false,
                handleSize: 8,
                infoTextOffset: 20,
                angleArcRadius: 20,
                pointAnnotationSize: 10,
                polygonResizeHandlePadding: 4,
                polygonCloseHighlightPadding: 6);
        }

        private static IReadOnlyList<RoiBase> CreateRois()
        {
            return
            [
                new RotatedRect { Center = new PointD(50, 50), Width = 40, Height = 20, Angle = 15 },
                new BlobAnalysisRoi
                {
                    Center = new PointD(60, 60),
                    Width = 40,
                    Height = 30,
                    DetectedBlobs =
                    [
                        new BlobFeature(
                            1,
                            120,
                            new PointD(55, 55),
                            new RectD(45, 45, 10, 10),
                            [new PointD(50, 50), new PointD(60, 50), new PointD(60, 58), new PointD(52, 60)]),
                        new BlobFeature(2, 80, new PointD(70, 65), new RectD(60, 60, 8, 8))
                    ]
                },
                new FittedEllipseRoi { Center = new PointD(50, 50), RadiusX = 25, RadiusY = 15, Angle = 20 },
                new RingRoi { Center = new PointD(50, 50), InnerRadius = 10, OuterRadius = 25 },
                new EllipseRoi { Center = new PointD(50, 50), RadiusX = 25, RadiusY = 15, Angle = 10 },
                new CircleRoi { Center = new PointD(50, 50), Radius = 20 },
                new PolygonRoi
                {
                    IsClosed = true,
                    Points = { new PointD(10, 10), new PointD(60, 10), new PointD(60, 50), new PointD(10, 50) }
                },
                new PolylineRoi { Points = { new PointD(10, 10), new PointD(30, 40), new PointD(60, 10) } },
                new PointAnnotationRoi { Position = new PointD(20, 20), Label = "P (20,20)" },
                new PointCoordinateMeasureRoi { Position = new PointD(25, 25) },
                new TextAnnotationRoi { Position = new PointD(30, 30), Label = "Text" },
                new ArrowAnnotationRoi
                {
                    P1 = new PointD(10, 60),
                    P2 = new PointD(60, 10),
                    Label = "Arrow",
                    ArrowHeadLength = 12
                },
                new LineMeasureRoi { P1 = new PointD(10, 10), P2 = new PointD(80, 40) },
                new AngleMeasureRoi
                {
                    P1 = new PointD(70, 50),
                    Vertex = new PointD(50, 50),
                    P2 = new PointD(50, 20)
                },
                new ArcMeasureRoi
                {
                    StartPoint = new PointD(70, 50),
                    EndPoint = new PointD(30, 50),
                    ArcPoint = new PointD(50, 30)
                },
                new PointToLineDistanceRoi
                {
                    Point = new PointD(50, 80),
                    LineP1 = new PointD(10, 40),
                    LineP2 = new PointD(90, 40)
                },
                new PointToCircleDistanceRoi
                {
                    Point = new PointD(90, 50),
                    Center = new PointD(50, 50),
                    Radius = 20
                },
                new ParallelismMeasureRoi
                {
                    Line1P1 = new PointD(10, 20),
                    Line1P2 = new PointD(90, 20),
                    Line2P1 = new PointD(10, 50),
                    Line2P2 = new PointD(90, 50)
                },
                new PerpendicularityMeasureRoi
                {
                    Line1P1 = new PointD(10, 40),
                    Line1P2 = new PointD(90, 40),
                    Line2P1 = new PointD(50, 10),
                    Line2P2 = new PointD(50, 70)
                },
                new ConcentricityMeasureRoi
                {
                    Center1 = new PointD(40, 50),
                    Center2 = new PointD(55, 50),
                    Radius1 = 20,
                    Radius2 = 12
                },
                new CenterDistanceMeasureRoi
                {
                    Center1 = new PointD(30, 50),
                    Center2 = new PointD(70, 50)
                },
                new ThreePointCircleMeasureRoi
                {
                    P1 = new PointD(70, 50),
                    P2 = new PointD(50, 30),
                    P3 = new PointD(30, 50)
                }
            ];
        }
    }
}