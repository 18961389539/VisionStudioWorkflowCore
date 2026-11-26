using System.Windows;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// 公差判定器测试：判定逻辑与持久化往返。
    /// Chinese: 验证 MeasurementTolerance 的合格/超差判定，以及通过插件注册表的持久化恢复。
    /// English: Tests for the measurement tolerance judgement and persistence round-trip.
    /// </summary>
    public class MeasurementToleranceTests
    {
        [Fact]
        public void IsWithinTolerance_WithinRange_ReturnsTrue()
        {
            var tolerance = new MeasurementTolerance { Nominal = 10, TolerancePlus = 0.5, ToleranceMinus = 0.5 };

            Assert.True(tolerance.IsWithinTolerance(9.5));
            Assert.True(tolerance.IsWithinTolerance(10.0));
            Assert.True(tolerance.IsWithinTolerance(10.5));
        }

        [Fact]
        public void IsWithinTolerance_OutOfRange_ReturnsFalse()
        {
            var tolerance = new MeasurementTolerance { Nominal = 10, TolerancePlus = 0.5, ToleranceMinus = 0.5 };

            Assert.False(tolerance.IsWithinTolerance(9.49));
            Assert.False(tolerance.IsWithinTolerance(10.51));
        }

        [Fact]
        public void InvalidTolerance_IsNotEvaluableInsteadOfPassingSilently()
        {
            var tolerance = new MeasurementTolerance { Nominal = 10, TolerancePlus = -0.1 };
            var roi = new LineMeasureRoi
            {
                P1 = new PointD(0, 0),
                P2 = new PointD(10, 0),
                Tolerance = tolerance
            };

            Assert.False(tolerance.IsValid);
            Assert.False(tolerance.IsWithinTolerance(10));
            RoiInspectionResult result = RoiInspectionEvaluator.Evaluate(roi, pixelSize: 1);
            Assert.Equal(RoiSpecificationStatus.NotEvaluable, result.SpecificationStatus);
        }

        [Fact]
        public void IsWithinTolerance_Disabled_AlwaysPasses()
        {
            var tolerance = new MeasurementTolerance();

            Assert.False(tolerance.IsEnabled);
            Assert.True(tolerance.IsWithinTolerance(double.NegativeInfinity));
            Assert.True(tolerance.IsWithinTolerance(12345.0));
        }

        [Fact]
        public void Tolerance_RoundTripThroughRegistry_PreservesValues()
        {
            RoiPluginRegistry registry = RoiPluginRegistry.CreateBuiltIn();
            var roi = new LineMeasureRoi
            {
                P1 = new PointD(0, 0),
                P2 = new PointD(10, 0),
                Tolerance = new MeasurementTolerance { Nominal = 12.5, TolerancePlus = 0.25, ToleranceMinus = 0.75 }
            };

            var data = new RoiPersistenceData();
            registry.FindByType(roi.GetType())!.PopulatePersistenceData(roi, data);
            data.PopulateCommonState(roi, "line-measure");
            var restored = (LineMeasureRoi)registry.FindByType(roi.GetType())!.CreateRoi(data);
            RoiPersistencePointExtensions.ApplyCommonState(restored, data);

            Assert.NotNull(restored.Tolerance);
            Assert.Equal(roi.Tolerance!.Nominal, restored.Tolerance!.Nominal);
            Assert.Equal(roi.Tolerance.TolerancePlus, restored.Tolerance.TolerancePlus);
            Assert.Equal(roi.Tolerance.ToleranceMinus, restored.Tolerance.ToleranceMinus);
        }

        [Fact]
        public void Tolerance_SurvivesCloneAndApplyFrom_AsIndependentCopy()
        {
            var line = new LineMeasureRoi
            {
                P1 = new PointD(0, 0),
                P2 = new PointD(10, 0),
                Tolerance = new MeasurementTolerance { Nominal = 5, TolerancePlus = 0.1, ToleranceMinus = 0.2 }
            };

            var clone = (LineMeasureRoi)line.Clone();

            Assert.Equal(5, clone.Tolerance!.Nominal);
            Assert.Equal(0.1, clone.Tolerance.TolerancePlus);
            Assert.Equal(0.2, clone.Tolerance.ToleranceMinus);

            // 公差面板会就地修改实例，快照必须持有独立副本，撤销才能还原。
            clone.Tolerance!.Nominal = 9;
            Assert.Equal(5, line.Tolerance!.Nominal);

            var restored = new LineMeasureRoi();
            restored.ApplyFrom(line);
            Assert.Equal(5, restored.Tolerance!.Nominal);
        }

        [Fact]
        public void Tolerance_SurvivesClone_ForCaliperTypeUsingApplyFrom()
        {
            var caliper = new LineCaliperMeasureRoi
            {
                Tolerance = new MeasurementTolerance { Nominal = 3.5 }
            };

            var clone = (LineCaliperMeasureRoi)caliper.Clone();

            Assert.Equal(3.5, clone.Tolerance!.Nominal);
        }

        [Fact]
        public void Tolerance_NewEdgeSelectionAndMinimumGap_PersistRoundTrip()
        {
            RoiPluginRegistry registry = RoiPluginRegistry.CreateBuiltIn();
            var lineCaliper = new LineCaliperMeasureRoi
            {
                P1 = new PointD(0, 0),
                P2 = new PointD(10, 0),
                EdgeSelection = 3
            };
            var widthCaliper = new CaliperMeasureRoi
            {
                P1 = new PointD(0, 0),
                P2 = new PointD(10, 0),
                MinimumEdgeGap = 4.5,
                HasExplicitCaliperRegion = true,
                CaliperCenter = new PointD(5, 0),
                CaliperAngleDegrees = 0,
                CaliperSearchRange = 8
            };

            var lineData = new RoiPersistenceData();
            registry.FindByType(lineCaliper.GetType())!.PopulatePersistenceData(lineCaliper, lineData);
            var restoredLine = (LineCaliperMeasureRoi)registry.FindByType(lineCaliper.GetType())!.CreateRoi(lineData);
            Assert.Equal(3, restoredLine.EdgeSelection);

            var widthData = new RoiPersistenceData();
            registry.FindByType(widthCaliper.GetType())!.PopulatePersistenceData(widthCaliper, widthData);
            var restoredWidth = (CaliperMeasureRoi)registry.FindByType(widthCaliper.GetType())!.CreateRoi(widthData);
            Assert.Equal(4.5, restoredWidth.MinimumEdgeGap);
        }
    }
}
