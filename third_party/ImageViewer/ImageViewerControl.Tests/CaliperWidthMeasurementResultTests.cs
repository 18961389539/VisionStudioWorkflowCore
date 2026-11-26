using System;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests
{
    public class CaliperWidthMeasurementResultTests
    {
        [Fact]
        public void ComputesStatisticsFromPerCaliperWidths()
        {
            var result = new CaliperWidthMeasurementResult([10, 12, 11, 13], centerDistance: 11.5);

            Assert.True(result.IsMeasured);
            Assert.Equal(4, result.SampleCount);
            Assert.Equal(10, result.Minimum);
            Assert.Equal(13, result.Maximum);
            Assert.Equal(11.5, result.Mean, 6);
            Assert.Equal(11.5, result.Median, 6);
            Assert.Equal(Math.Sqrt(1.25), result.StandardDeviation, 6);
            Assert.Equal(3, result.Range);
            Assert.Equal(11.5, result.CenterDistance);
        }

        [Fact]
        public void FiltersInvalidWidthsAndProvidesEmptyResult()
        {
            var result = new CaliperWidthMeasurementResult([double.NaN, 0, -1, 5], centerDistance: double.NaN);

            Assert.Equal(1, result.SampleCount);
            Assert.Equal(5, result.Mean);
            Assert.Equal(0, result.CenterDistance);
            Assert.Same(CaliperWidthMeasurementResult.Empty, new CaliperMeasureRoi().WidthMeasurement);
        }

        [Fact]
        public void CloneAndClearPreserveAndResetWidthMeasurement()
        {
            var roi = new CaliperMeasureRoi();
            roi.ApplyWidthMeasurement(new CaliperWidthMeasurementResult([7, 8, 9], 8));

            var clone = (CaliperMeasureRoi)roi.Clone();
            Assert.Equal(3, clone.WidthMeasurement.SampleCount);
            Assert.Equal(8, clone.WidthMeasurement.Mean);

            roi.ClearDetectedEdges();
            Assert.False(roi.WidthMeasurement.IsMeasured);
        }

        [Fact]
        public void RoiInfoShowsDetectedWidthStatisticsWhenAvailable()
        {
            var roi = new CaliperMeasureRoi
            {
                P1 = new PointD(0, 0),
                P2 = new PointD(20, 0),
                HasDetectedEdges = true,
                ValidCaliperCount = 4
            };
            roi.ApplyWidthMeasurement(new CaliperWidthMeasurementResult([9, 10, 11, 10], centerDistance: 10));

            string info = RoiInfoService.BuildInfo(
                roi,
                bitmap: null,
                pixelSize: 1,
                physicalUnit: null,
                pluginRegistry: RoiPluginRegistry.CreateBuiltIn(),
                includeStatistics: false);

            Assert.Contains("检测宽度", info);
            Assert.Contains("均值:10.00 px", info);
            Assert.Contains("标准差", info);
            Assert.Contains("样本:4", info);
        }

        [Fact]
        public void CsvExportIncludesDetectedWidthStatistics()
        {
            var roi = new CaliperMeasureRoi
            {
                P1 = new PointD(0, 0),
                P2 = new PointD(20, 0)
            };
            roi.ApplyWidthMeasurement(new CaliperWidthMeasurementResult([9, 10, 11], centerDistance: 10));

            string csv = RoiAnalysisExportService.BuildCsv([roi], bitmap: null, pixelSize: 1, physicalUnit: null);

            Assert.Contains("Requested=20.00 px", csv);
            Assert.Contains("Mean=10.00 px", csv);
            Assert.Contains("Samples=3", csv);
        }
    }
}
