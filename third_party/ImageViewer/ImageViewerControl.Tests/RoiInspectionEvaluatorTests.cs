using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Services;
using ImageViewer.Utils;
using Xunit;

namespace ImageViewerControl.Tests
{
    public class RoiInspectionEvaluatorTests
    {
        [Fact]
        public void DetectedWidthUsesDetectionAndSpecificationStatusesIndependently()
        {
            var roi = new CaliperMeasureRoi
            {
                P1 = new PointD(0, 0),
                P2 = new PointD(20, 0),
                HasDetectedEdges = true,
                Tolerance = new MeasurementTolerance { Nominal = 10, TolerancePlus = 0.5, ToleranceMinus = 0.5 }
            };
            roi.ApplyWidthMeasurement(new CaliperWidthMeasurementResult([9.9, 10.0, 10.1], centerDistance: 10));

            RoiInspectionResult result = RoiInspectionEvaluator.Evaluate(roi, pixelSize: 1);

            Assert.Equal(RoiDetectionStatus.Passed, result.DetectionStatus);
            Assert.Equal(RoiSpecificationStatus.Passed, result.SpecificationStatus);
            Assert.Equal(10, result.MeasuredValue);
            Assert.Equal(10, result.NominalValue);
        }

        [Fact]
        public void UnmeasuredCircularCaliperCannotProduceSpecificationDecision()
        {
            var roi = new CircularCaliperMeasureRoi
            {
                Radius = 5,
                Tolerance = new MeasurementTolerance { Nominal = 5, TolerancePlus = 0.1, ToleranceMinus = 0.1 }
            };

            RoiInspectionResult result = RoiInspectionEvaluator.Evaluate(roi, pixelSize: 1);

            Assert.Equal(RoiDetectionStatus.NotMeasured, result.DetectionStatus);
            Assert.Equal(RoiSpecificationStatus.NotEvaluable, result.SpecificationStatus);
            Assert.Null(result.MeasuredValue);
        }

        [Fact]
        public void GroundTruthLineMeasurement_AppliesPixelScaleAndTolerance()
        {
            var roi = new LineMeasureRoi
            {
                P1 = new PointD(10, 20),
                P2 = new PointD(13, 24),
                Tolerance = new MeasurementTolerance { Nominal = 2.5, TolerancePlus = 0.01, ToleranceMinus = 0.01 }
            };

            RoiInspectionResult result = RoiInspectionEvaluator.Evaluate(roi, pixelSize: 0.5);

            Assert.Equal(2.5, result.MeasuredValue!.Value, precision: 10);
            Assert.Equal(RoiDetectionStatus.NotApplicable, result.DetectionStatus);
            Assert.Equal(RoiSpecificationStatus.Passed, result.SpecificationStatus);
        }

        [Fact]
        public void ParallelismProbe_ProducesAngularMeasuredValueAndEvaluatesTolerance()
        {
            // 平行度 ROI 的主测量值是"两线夹角差（度）"。此前它不在测量值分支里，测得值恒为 null，
            // 配了公差也永远 NotEvaluable —— 这里锁住"公差判定真正生效"。
            var roi = new ParallelismMeasureRoi
            {
                Line1P1 = new PointD(0, 0),
                Line1P2 = new PointD(10, 0),
                Line2P1 = new PointD(0, 5),
                Line2P2 = new PointD(10, 6),
                Tolerance = new MeasurementTolerance { Nominal = 0, TolerancePlus = 10, ToleranceMinus = 10 }
            };

            RoiInspectionResult result = RoiInspectionEvaluator.Evaluate(roi, pixelSize: 1);

            Assert.NotNull(result.MeasuredValue);
            Assert.Equal(roi.AngleDifference, result.MeasuredValue!.Value, precision: 10);
            Assert.True(result.MeasuredValue.Value > 0);
            Assert.Equal(RoiSpecificationStatus.Passed, result.SpecificationStatus);
        }

        [Fact]
        public void ParallelismProbe_OutsideTolerance_Fails()
        {
            var roi = new ParallelismMeasureRoi
            {
                Line1P1 = new PointD(0, 0),
                Line1P2 = new PointD(10, 0),
                Line2P1 = new PointD(0, 5),
                Line2P2 = new PointD(10, 10),
                Tolerance = new MeasurementTolerance { Nominal = 0, TolerancePlus = 1, ToleranceMinus = 1 }
            };

            RoiInspectionResult result = RoiInspectionEvaluator.Evaluate(roi, pixelSize: 1);

            Assert.NotNull(result.MeasuredValue);
            Assert.Equal(RoiSpecificationStatus.Failed, result.SpecificationStatus);
        }

        [Fact]
        public void ConcentricityProbe_MeasuresToleranceZoneDiameterNotEccentricity()
        {
            // ISO 1101 的同心度公差带是"直径 t 的圆"，所以可评估量是 t = 2e，而不是偏心量 e。
            // 直接拿 e 当测得值与公差比较会宽松一倍。
            var roi = new ConcentricityMeasureRoi
            {
                Center1 = new PointD(0, 0),
                Radius1 = 10,
                Center2 = new PointD(3, 4),
                Radius2 = 10,
                Tolerance = new MeasurementTolerance { Nominal = 5, TolerancePlus = 0.5, ToleranceMinus = 0.5 }
            };

            Assert.Equal(5.0, roi.CenterDistance, precision: 10);

            RoiInspectionResult result = RoiInspectionEvaluator.Evaluate(roi, pixelSize: 0.5);

            Assert.Equal(5.0, result.MeasuredValue!.Value, precision: 10);
            Assert.Equal(RoiSpecificationStatus.Passed, result.SpecificationStatus);
        }

        [Fact]
        public void PerpendicularityProbe_ProducesAngularDeviationInDegrees()
        {
            var roi = new PerpendicularityMeasureRoi
            {
                Line1P1 = new PointD(0, 0),
                Line1P2 = new PointD(10, 0),
                Line2P1 = new PointD(0, 0),
                Line2P2 = new PointD(0, 10),
                Tolerance = new MeasurementTolerance { Nominal = 0, TolerancePlus = 1, ToleranceMinus = 1 }
            };

            RoiInspectionResult result = RoiInspectionEvaluator.Evaluate(roi, pixelSize: 1);

            Assert.Equal(0.0, result.MeasuredValue!.Value, precision: 6);
            Assert.Equal(RoiSpecificationStatus.Passed, result.SpecificationStatus);
        }

        [Fact]
        public void AngleMeasureProbe_ProducesDegreesAndIgnoresPixelSize()
        {
            var roi = new AngleMeasureRoi
            {
                P1 = new PointD(10, 0),
                Vertex = new PointD(0, 0),
                P2 = new PointD(0, 10),
                Tolerance = new MeasurementTolerance { Nominal = 90, TolerancePlus = 0.5, ToleranceMinus = 0.5 }
            };

            RoiInspectionResult atPixelSizeOne = RoiInspectionEvaluator.Evaluate(roi, pixelSize: 1);
            RoiInspectionResult atPixelSizeHalf = RoiInspectionEvaluator.Evaluate(roi, pixelSize: 0.5);

            Assert.Equal(90.0, atPixelSizeOne.MeasuredValue!.Value, precision: 6);
            // 角度不随像素尺寸缩放：两种标定必须给出同一个角度值。
            Assert.Equal(atPixelSizeOne.MeasuredValue.Value, atPixelSizeHalf.MeasuredValue!.Value, precision: 6);
            Assert.Equal(RoiSpecificationStatus.Passed, atPixelSizeOne.SpecificationStatus);
        }

        [Fact]
        public void ArcCaliperProbe_MeasuresArcLengthNotRadius()
        {
            // 弧形卡尺是 CircularCaliperMeasureRoi 的子类：必须先于圆卡尺匹配，否则会错给半径。
            var roi = new ArcCaliperMeasureRoi
            {
                Center = new PointD(0, 0),
                Radius = 10,
                StartAngle = 0,
                SweepAngle = 90,
                Tolerance = new MeasurementTolerance { Nominal = 15.708, TolerancePlus = 0.01, ToleranceMinus = 0.01 }
            };
            roi.SetDetectedCircle(new PointD(0, 0), 10);
            roi.HasDetectedEdges = true;

            RoiInspectionResult result = RoiInspectionEvaluator.Evaluate(roi, pixelSize: 1);

            // 弧长 = 10 × (π/2) ≈ 15.708，而不是半径 10。
            Assert.Equal(10 * Math.PI / 2, result.MeasuredValue!.Value, precision: 6);
            Assert.Equal(RoiSpecificationStatus.Passed, result.SpecificationStatus);
        }

        [Fact]
        public void GeometricToleranceStyleProbes_AreFlaggedForUiDisclaimer()
        {
            Assert.True(MeasureZones.IsGeometricToleranceStyle(new ParallelismMeasureRoi()));
            Assert.True(MeasureZones.IsGeometricToleranceStyle(new PerpendicularityMeasureRoi()));
            Assert.True(MeasureZones.IsGeometricToleranceStyle(new ConcentricityMeasureRoi()));
            Assert.False(MeasureZones.IsGeometricToleranceStyle(new LineMeasureRoi()));
        }

        [Fact]
        public void AngularProbes_AreFlaggedAsAngularUnits()
        {
            Assert.True(MeasureZones.IsAngularMeasuredValue(new ParallelismMeasureRoi()));
            Assert.True(MeasureZones.IsAngularMeasuredValue(new PerpendicularityMeasureRoi()));
            Assert.True(MeasureZones.IsAngularMeasuredValue(new AngleMeasureRoi()));
            Assert.False(MeasureZones.IsAngularMeasuredValue(new ConcentricityMeasureRoi()));
            Assert.False(MeasureZones.IsAngularMeasuredValue(new LineMeasureRoi()));
        }

        [Fact]
        public void InfoPanel_ShowsToleranceZoneDiameterForConcentricity_AndADisclaimer()
        {
            // 界面显示的量必须与判定比较的量一致：同心度判定比的是 2×中心距。
            var roi = new ConcentricityMeasureRoi
            {
                Center1 = new PointD(0, 0),
                Radius1 = 10,
                Center2 = new PointD(3, 4),
                Radius2 = 10
            };

            string info = RoiInfoService.BuildInfo(
                roi,
                bitmap: null,
                pixelSize: 1,
                physicalUnit: "mm",
                pluginRegistry: RoiPluginRegistry.CreateBuiltIn(),
                includeStatistics: false);

            Assert.Contains("10", info);
            Assert.Contains(UiText.Get("InfoLineGeometricToleranceNote"), info);
        }

        [Fact]
        public void InfoPanel_FlagsAngularProbesAsDegreeComparisons()
        {
            var roi = new ParallelismMeasureRoi
            {
                Line1P1 = new PointD(0, 0),
                Line1P2 = new PointD(10, 0),
                Line2P1 = new PointD(0, 5),
                Line2P2 = new PointD(10, 6)
            };

            string info = RoiInfoService.BuildInfo(
                roi,
                bitmap: null,
                pixelSize: 1,
                physicalUnit: "mm",
                pluginRegistry: RoiPluginRegistry.CreateBuiltIn(),
                includeStatistics: false);

            Assert.Contains(UiText.Get("InfoLineAngularUnitNote"), info);
            Assert.Contains(UiText.Get("InfoLineGeometricToleranceNote"), info);
        }

        [Fact]
        public void PropertyEditors_ForAngularAndConcentricProbes_ExposeToleranceAndMeasuredQuantityNote()
        {
            WpfTestRunner.Run(() =>
            {
                // 这些 ROI 过去连公差编辑器都没有：判定失效 + 用户无法配置，两个问题一并锁住。
                var parallelism = new ParallelismMeasureRoi();
                var perpendicularity = new PerpendicularityMeasureRoi();
                var concentricity = new ConcentricityMeasureRoi();
                var angle = new AngleMeasureRoi();
                var centerDistance = new CenterDistanceMeasureRoi();

                foreach (RoiBase roi in new RoiBase[] { parallelism, perpendicularity, concentricity, angle, centerDistance })
                {
                    FrameworkElement editor = BuiltInRoiPropertyEditorFactory.CreateEditor(roi)!;
                    Assert.Contains(UiText.Get("EditorToleranceGroup"), CollectText(editor));
                }

                Assert.Contains(UiText.Get("EditorMeasuredQuantityConcentricity"), CollectText(BuiltInRoiPropertyEditorFactory.CreateEditor(concentricity)!));
                Assert.Contains(UiText.Get("EditorMeasuredQuantityParallelism"), CollectText(BuiltInRoiPropertyEditorFactory.CreateEditor(parallelism)!));
                Assert.Contains(UiText.Get("EditorMeasuredQuantityPerpendicularity"), CollectText(BuiltInRoiPropertyEditorFactory.CreateEditor(perpendicularity)!));
                Assert.Contains(UiText.Get("EditorMeasuredQuantityAngle"), CollectText(BuiltInRoiPropertyEditorFactory.CreateEditor(angle)!));
            });
        }

        private static string CollectText(DependencyObject element)
        {
            var builder = new System.Text.StringBuilder();
            Collect(element);
            return builder.ToString();

            void Collect(DependencyObject node)
            {
                if (node is TextBlock textBlock)
                {
                    builder.Append(' ').Append(textBlock.Text);
                }

                int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < count; i++)
                {
                    Collect(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
                }
            }
        }

        [Fact]
    public void CsvExportsBothStatusesAsSeparateColumns()
        {
            var roi = new LineMeasureRoi
            {
                P1 = new PointD(0, 0),
                P2 = new PointD(10, 0),
                Tolerance = new MeasurementTolerance { Nominal = 10, TolerancePlus = 0, ToleranceMinus = 0 }
            };

            string csv = RoiAnalysisExportService.BuildCsv([roi], bitmap: null, pixelSize: 1, physicalUnit: "mm");

            Assert.Contains("DetectionStatus,SpecificationStatus,MeasuredValue,NominalValue", csv);
            Assert.Contains("\"NotApplicable\",\"Passed\",10,10", csv);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void CsvRejectsInvalidPixelSize(double pixelSize)
        {
            var roi = new LineMeasureRoi { P1 = new PointD(0, 0), P2 = new PointD(1, 0) };

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                RoiAnalysisExportService.BuildCsv([roi], bitmap: null, pixelSize, physicalUnit: "mm"));
        }

        [Fact]
        public void InfoRejectsInvalidPixelSize()
        {
            var roi = new LineMeasureRoi { P1 = new PointD(0, 0), P2 = new PointD(1, 0) };

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                RoiInfoService.BuildInfo(roi, bitmap: null, pixelSize: 0, physicalUnit: "mm", pluginRegistry: RoiPluginRegistry.CreateBuiltIn(), includeStatistics: false));
        }

        [Fact]
        public void InfoAppendsToleranceJudgementWhenPluginSuppliesCustomLines()
        {
            var roi = new LineMeasureRoi
            {
                P1 = new PointD(0, 0),
                P2 = new PointD(10, 0),
                Tolerance = new MeasurementTolerance { Nominal = 10, TolerancePlus = 0.1, ToleranceMinus = 0.1 }
            };
            var registry = new RoiPluginRegistry();
            registry.Register(new CustomInfoPlugin());

            string info = RoiInfoService.BuildInfo(roi, bitmap: null, pixelSize: 1, physicalUnit: "mm", pluginRegistry: registry, includeStatistics: false);

            Assert.Contains("custom plugin info", info);
            Assert.Contains("合格", info);
        }

        [Fact]
        public void InfoShowsCalibrationAccuracyMetadataEvenWithoutDistortion()
        {
            // "比例标定 + 精度记录"是合法组合：没有畸变系数也必须能看到这次标定准到什么程度。
            var roi = new LineMeasureRoi { P1 = new PointD(0, 0), P2 = new PointD(10, 0) };
            var calibration = new CameraCalibration
            {
                NormalizationRadius = 100,
                ReprojectionErrorRms = 0.0834,
                MeasurementUncertaintyPixels = 0.25,
                CalibrationId = "CERT-2026-001"
            };

            string info = RoiInfoService.BuildInfo(roi, bitmap: null, pixelSize: 1, physicalUnit: "mm", pluginRegistry: RoiPluginRegistry.CreateBuiltIn(), includeStatistics: false, calibration: calibration);

            Assert.Contains(UiText.FormatInvariant("InfoLineCalibrationAccuracy", 0.0834, 0.25), info);
            Assert.Contains(UiText.FormatInvariant("InfoLineCalibrationIdentity", "CERT-2026-001"), info);
            Assert.DoesNotContain(UiText.Get("InfoLineCalibrationDistortion"), info);
        }

        [Fact]
        public void InfoShowsDistortionAndAccuracyTogetherWhenBothPresent()
        {
            var roi = new LineMeasureRoi { P1 = new PointD(0, 0), P2 = new PointD(10, 0) };
            var calibration = new CameraCalibration
            {
                NormalizationRadius = 100,
                K1 = 0.01,
                K2 = -0.002,
                K3 = 0.0003,
                TangentialP1 = 0.00004,
                TangentialP2 = -0.00005,
                ReprojectionErrorRms = 0.08,
                MeasurementUncertaintyPixels = 0.2
            };

            string info = RoiInfoService.BuildInfo(roi, bitmap: null, pixelSize: 1, physicalUnit: "mm", pluginRegistry: RoiPluginRegistry.CreateBuiltIn(), includeStatistics: false, calibration: calibration);

            // 扩展模型行已包含精度，不应再重复一行
            Assert.Contains(UiText.FormatInvariant("InfoLineCalibrationDistortionExtended", 0.01, -0.002, 0.0003, 0.00004, -0.00005, 0.08, 0.2), info);
            Assert.DoesNotContain(UiText.FormatInvariant("InfoLineCalibrationAccuracy", 0.08, 0.2), info);
        }

        [Fact]
        public void InfoWarnsWhenCalibrationIsInvalid()
        {
            var roi = new LineMeasureRoi { P1 = new PointD(0, 0), P2 = new PointD(10, 0) };
            var calibration = new CameraCalibration
            {
                NormalizationRadius = 100,
                K1 = 0.01,
                ReprojectionErrorRms = double.NaN
            };

            string info = RoiInfoService.BuildInfo(roi, bitmap: null, pixelSize: 1, physicalUnit: "mm", pluginRegistry: RoiPluginRegistry.CreateBuiltIn(), includeStatistics: false, calibration: calibration);

            // 非法标定必须明确告知尺寸不可评估，不能显示正常测量值或标定身份。
            Assert.Contains(UiText.Get("InfoLineCalibrationInvalid"), info);
            Assert.DoesNotContain(UiText.Get("InfoLineCalibrationIdentity").Replace("{0}", string.Empty, StringComparison.Ordinal), info);
        }

        [Fact]
        public void SummaryReportsPrecisionForTolerancedRois()
        {
            var roi = new LineMeasureRoi
            {
                Label = "孔径",
                P1 = new PointD(0, 0),
                P2 = new PointD(10, 0),
                Tolerance = new MeasurementTolerance { Nominal = 10, TolerancePlus = 0.2, ToleranceMinus = 0.2 }
            };
            var calibration = new CameraCalibration
            {
                NormalizationRadius = 100,
                ReprojectionErrorRms = 0.08,
                MeasurementUncertaintyPixels = 0.2
            };

            string summary = RoiAnalysisExportService.BuildSummary([roi], bitmap: null, pixelSize: 0.5, physicalUnit: "mm", calibration: calibration);

            // 单次测量必须带出"这次能信到什么程度"，且标定不确定度要换算成物理单位（0.2 px × 0.5 mm/px = 0.1 mm）。
            Assert.Contains("[Precision]", summary);
            Assert.Contains("calibrated=True", summary);
            Assert.Contains("0.1000 mm", summary);
            Assert.Contains("0.0800 px", summary);
            Assert.Contains("孔径", summary);
            Assert.Contains("repeatability=1 sample", summary);
        }

        [Fact]
        public void SummaryOmitsPrecisionSectionWhenNoToleranceConfigured()
        {
            var roi = new LineMeasureRoi { P1 = new PointD(0, 0), P2 = new PointD(10, 0) };

            string summary = RoiAnalysisExportService.BuildSummary([roi], bitmap: null, pixelSize: 1, physicalUnit: "mm");

            Assert.DoesNotContain("[Precision]", summary);
        }

        [Fact]
        public void SummaryMarksUncalibratedPrecisionAsUnavailable()
        {
            var roi = new LineMeasureRoi
            {
                P1 = new PointD(0, 0),
                P2 = new PointD(10, 0),
                Tolerance = new MeasurementTolerance { Nominal = 10, TolerancePlus = 0.2, ToleranceMinus = 0.2 }
            };

            string summary = RoiAnalysisExportService.BuildSummary([roi], bitmap: null, pixelSize: 1, physicalUnit: "mm");

            // 没有标定就不该编造不确定度——必须显式标为 n/a。
            Assert.Contains("calibrated=False", summary);
            Assert.Contains("calibrationUncertainty=n/a", summary);
            Assert.Contains("combinedUncertainty(k=2)=n/a", summary);
        }

        [Fact]
        public void InvalidEnabledCalibrationCannotProduceSpecificationDecision()        {
            var roi = new LineMeasureRoi
            {
                P1 = new PointD(0, 0),
                P2 = new PointD(10, 0),
                Tolerance = new MeasurementTolerance { Nominal = 10, TolerancePlus = 0.1, ToleranceMinus = 0.1 }
            };
            var calibration = new CameraCalibration
            {
                PrincipalX = 0,
                PrincipalY = 0,
                NormalizationRadius = 1,
                K1 = 0.1,
                ReprojectionErrorRms = double.NaN
            };

            RoiInspectionResult result = RoiInspectionEvaluator.Evaluate(roi, 1, calibration);

            Assert.Null(result.MeasuredValue);
            Assert.Equal(RoiSpecificationStatus.NotEvaluable, result.SpecificationStatus);
        }

        private sealed class CustomInfoPlugin : IRoiPlugin
        {
            public string TypeKey => "custom-line-info";

            public Type RoiType => typeof(LineMeasureRoi);

            public int HitTestOrder => 0;

            public IReadOnlyList<RoiToolDescriptor> DrawingTools => Array.Empty<RoiToolDescriptor>();

            public ImageViewer.Abstractions.IRoiBehavior Behavior => null!;

            public ImageViewer.Rendering.IRoiRenderer Renderer => null!;

            public IEnumerable<RoiBase> GetRois(ImageViewer.ViewModels.ImageViewerViewModel viewModel) => throw new NotSupportedException();

            public void ClearCollection(ImageViewer.ViewModels.ImageViewerViewModel viewModel) => throw new NotSupportedException();

            public bool AddToCollection(ImageViewer.ViewModels.ImageViewerViewModel viewModel, RoiBase roi) => throw new NotSupportedException();

            public bool RemoveFromCollection(ImageViewer.ViewModels.ImageViewerViewModel viewModel, RoiBase roi) => throw new NotSupportedException();

            public RoiBase CreateRoi(RoiPersistenceData data) => throw new NotSupportedException();

            public void PopulatePersistenceData(RoiBase roi, RoiPersistenceData data) => throw new NotSupportedException();

            public IReadOnlyList<string> BuildInfoLines(RoiBase roi, BitmapSource? bitmap, double pixelSize, string? physicalUnit)
                => ["custom plugin info"];

            public FrameworkElement? CreatePropertyEditor(RoiBase roi) => null;
        }
    }
}
