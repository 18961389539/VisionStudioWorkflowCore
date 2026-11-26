using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Models;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests
{
    public class SpcMeasurementSeriesBuilderTests
    {
        [Fact]
        public void BuildFromRois_GroupsRepeatedQuantitiesAndKeepsSampleOrder()
        {
            var tolerance = new MeasurementTolerance { Nominal = 7, TolerancePlus = 0.5, ToleranceMinus = 0.5 };
            var rois = new RoiBase[]
            {
                CreateLine("第一段", 3, tolerance),
                CreateCircle("孔", 5),
                CreateLine("第二段", 5, tolerance)
            };

            IReadOnlyList<SpcMeasurementSeries> series = SpcMeasurementSeriesBuilder.BuildFromRois(rois, pixelSize: 2, physicalUnit: "mm", calibration: null);

            // 圆只有一个样本，离散度无从谈起，不应出现在结果里。
            SpcMeasurementSeries length = Assert.Single(series);
            Assert.Equal("长度", length.Label);
            Assert.Equal("mm", length.Unit);
            double[] expected = [6d, 10d];
            Assert.Equal(expected, length.Values);
            Assert.Equal(6.5, length.LowerSpecificationLimit);
            Assert.Equal(7.5, length.UpperSpecificationLimit);
        }

        [Fact]
        public void BuildFromRois_RequiresAtLeastTwoMeasuredValues()
        {
            var rois = new RoiBase[]
            {
                CreateLine("唯一", 4, tolerance: null),
                new CaliperMeasureRoi() // 未检测 → 无测得值
            };

            IReadOnlyList<SpcMeasurementSeries> series = SpcMeasurementSeriesBuilder.BuildFromRois(rois, pixelSize: 1, physicalUnit: "mm", calibration: null);

            Assert.Empty(series);
        }

        [Fact]
        public void BuildFromRois_ConflictingTolerancesDisableSpecificationLimits()
        {
            var rois = new RoiBase[]
            {
                CreateLine("a", 4, new MeasurementTolerance { Nominal = 5, TolerancePlus = 0.1, ToleranceMinus = 0.1 }),
                CreateLine("b", 6, new MeasurementTolerance { Nominal = 6, TolerancePlus = 0.1, ToleranceMinus = 0.1 })
            };

            SpcMeasurementSeries length = Assert.Single(SpcMeasurementSeriesBuilder.BuildFromRois(rois, pixelSize: 1, physicalUnit: "mm", calibration: null));

            Assert.Null(length.LowerSpecificationLimit);
            Assert.Null(length.UpperSpecificationLimit);
        }

        [Fact]
        public void BuildFromBatchRows_GroupsByMeasurementItemKeyAndSkipsUndecodedRows()
        {
            BitmapSource bitmap = CreateBitmap();
            var rows = new[]
            {
                CreateRow(bitmap, pixelLength: 3, measurementItemKey: "slot-1"),
                CreateRow(bitmap, pixelLength: 5, measurementItemKey: "slot-1"),
                CreateRow(bitmap: null, pixelLength: 4, measurementItemKey: "slot-1"),
                CreateRow(bitmap, pixelLength: 2, measurementItemKey: "slot-2")
            };

            IReadOnlyList<SpcMeasurementSeries> series = SpcMeasurementSeriesBuilder.BuildFromBatchRows(rows);

            SpcMeasurementSeries slot = Assert.Single(series);
            Assert.Equal("slot-1", slot.Key);
            double[] expected = [3d, 5d];
            Assert.Equal(expected, slot.Values);
        }

        [Fact]
        public void Formatter_RendersTheCapabilityTableFromTheSameCalculation()
        {
            var series = new SpcMeasurementSeries("k", "长度", "mm", [9, 10, 11], 7, 13);

            string markdown = SpcReportFormatter.BuildMarkdown([series]);

            Assert.Contains("## 过程能力 (SPC)", markdown);
            Assert.Contains("| 长度 | mm | 3 |", markdown);
            Assert.Contains("[7, 13] mm", markdown);

            IReadOnlyList<Dictionary<string, object?>> metadata = SpcReportFormatter.BuildMetadata([series]);
            Dictionary<string, object?> item = Assert.Single(metadata);
            Assert.Equal("k", item["key"]);
            Assert.Equal(3, item["sampleCount"]);
            Assert.NotNull(item["cp"]);
            Assert.NotNull(item["cpk"]);
            Assert.Equal(1d, (double)item["pp"]!, 10);
        }

        [Fact]
        public void Formatter_OmitsTheSectionWhenNoSeriesIsAvailable()
        {
            Assert.Equal(string.Empty, SpcReportFormatter.BuildMarkdown([]));
            Assert.Empty(SpcReportFormatter.BuildMetadata([]));
        }

        private static LineMeasureRoi CreateLine(string label, double pixelLength, MeasurementTolerance? tolerance) => new()
        {
            Label = label,
            P1 = new PointD(0, 0),
            P2 = new PointD(pixelLength, 0),
            Tolerance = tolerance
        };

        private static CircleRoi CreateCircle(string label, double radius) => new()
        {
            Label = label,
            Radius = radius
        };

        private static RoiAnalysisBatchCsvRow CreateRow(BitmapSource? bitmap, double pixelLength, string measurementItemKey) =>
            RoiAnalysisExportService.CaptureBatchRow(
                "image.png", "Completed", null, CreateLine("长度", pixelLength, tolerance: null), bitmap,
                pixelSize: 1, physicalUnit: "mm", calibration: null, sourceIndex: 0, measurementItemKey);

        private static BitmapSource CreateBitmap()
        {
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
