using System;
using System.Linq;
using ImageViewer.Core.Analysis;
using ImageViewer.Models;
using Xunit;

namespace ImageViewer.Core.Tests
{
    /// <summary>
    /// 多轮重复性研究的守卫测试。
    /// Chinese: 关键不是"算得对"（那是 ①-2 已经测过的 σ），而是三件容易退化的语义：
    /// ① 样本不足不能给结论；② 未测得值不能当成 0；③ "占公差比"必须真的把离散度和公差连起来。
    /// </summary>
    [Trait("Category", "Unit")]
    public sealed class RepeatabilityStudyTests
    {
        private static RepeatabilityStudyBuilder.RoundSample Sample(string key, double value, int source) =>
            new(key, value, source);

        private static RepeatabilityStudyBuilder.RoundSample Sample(string key, double value, int source, string label) =>
            new(key, value, source, label);

        private static readonly RepeatabilityStudyOptions SameWorkpiece =
            new(RepeatabilityStudyContext.SameWorkpiece, "part-001");

        [Fact]
        public void SingleSamplePerItemYieldsNoConclusiveResult()
        {
            RepeatabilityStudy study = RepeatabilityStudyBuilder.Build(
                [Sample("孔径", 10.0, 0), Sample("宽度", 5.0, 0)],
                unit: "mm",
                options: SameWorkpiece);

            // 每项只有一个样本 ⇒ 离散度无从谈起，必须显式说"无法结论"而不是报 0。
            Assert.False(study.HasConclusiveResult);
            Assert.Equal(0, study.ConclusiveItemCount);
            Assert.Equal(2, study.InconclusiveItemCount);
            Assert.All(study.Items, item => Assert.Null(item.Statistics.ExpandedUncertainty));

            string markdown = RepeatabilityStudyRenderer.RenderMarkdown(study);
            Assert.Contains("无法给出重复性结论", markdown);
        }

        [Fact]
        public void RepeatedMeasurementsProduceSampleStatisticsPerItem()
        {
            RepeatabilityStudy study = RepeatabilityStudyBuilder.Build(
                [
                    Sample("孔径", 10.0, 0), Sample("孔径", 11.0, 1), Sample("孔径", 12.0, 2),
                    Sample("宽度", 5.0, 0), Sample("宽度", 5.0, 1)
                ],
                unit: "mm",
                options: SameWorkpiece);

            Assert.Equal(3, study.SourceCount);
            Assert.Equal(2, study.ConclusiveItemCount);

            RepeatabilityStudyItem bore = study.Items.Single(item => item.Label == "孔径");
            Assert.Equal(3, bore.Statistics.SampleCount);
            Assert.Equal(11.0, bore.Statistics.Mean);
            // 样本 σ（n-1）= sqrt(2/2) = 1，不是总体 σ sqrt(2/3)。
            Assert.Equal(1.0, bore.Statistics.StandardDeviation, 10);
            Assert.Equal(2.0, bore.Statistics.ExpandedUncertainty);

            RepeatabilityStudyItem width = study.Items.Single(item => item.Label == "宽度");
            // 完全一致 ⇒ σ = 0，扩展不确定度也是 0（这里 0 是真实结论，不是"缺数据"）。
            Assert.Equal(0.0, width.Statistics.StandardDeviation, 10);
            Assert.Equal(0.0, width.Statistics.ExpandedUncertainty);
        }

        [Fact]
        public void NonFiniteValuesAreExcludedRatherThanSkewingTheMean()
        {
            RepeatabilityStudy study = RepeatabilityStudyBuilder.Build(
                [Sample("孔径", 10.0, 0), Sample("孔径", double.NaN, 1), Sample("孔径", 12.0, 2)],
                unit: "mm",
                options: SameWorkpiece);

            RepeatabilityStudyItem bore = study.Items.Single();
            // NaN 混进均值会污染整个统计量，必须剔除。
            Assert.Equal(2, bore.Statistics.SampleCount);
            Assert.Equal(11.0, bore.Statistics.Mean, 10);
        }

        [Fact]
        public void RepeatabilityIsExpressedAsAShareOfToleranceBand()
        {
            RepeatabilityStudy study = RepeatabilityStudyBuilder.Build(
                [Sample("孔径", 10.0, 0), Sample("孔径", 10.1, 1), Sample("孔径", 9.9, 2)],
                toleranceLookup: _ => (10.0, 0.5, 0.5),
                quantityLookup: _ => "长度",
                unit: "mm",
                options: SameWorkpiece);

            RepeatabilityStudyItem bore = study.Items.Single();
            Assert.Equal(1.0, bore.ToleranceBand);
            // σ = 0.1，扩展不确定度 0.2 ⇒ 占 1.0 公差带的 20%。
            Assert.Equal(20.0, bore.RepeatabilityShareOfTolerancePercent!.Value, 6);
            Assert.False(bore.RepeatabilityConsumesEntireTolerance);
            Assert.False(bore.ExceedsOneThirdOfTolerance);
        }

        [Fact]
        public void WideSpreadAgainstTightToleranceIsFlaggedAsConsumingTheBand()
        {
            RepeatabilityStudy study = RepeatabilityStudyBuilder.Build(
                [Sample("孔径", 10.0, 0), Sample("孔径", 10.6, 1), Sample("孔径", 9.4, 2)],
                toleranceLookup: _ => (10.0, 0.05, 0.05),
                unit: "mm",
                options: SameWorkpiece);

            RepeatabilityStudyItem bore = study.Items.Single();
            // σ ≈ 0.6，扩展不确定度 ≈ 1.2，公差带仅 0.1 ⇒ 远超 100%。
            Assert.True(bore.RepeatabilityConsumesEntireTolerance);
            Assert.True(bore.ExceedsOneThirdOfTolerance);
            Assert.Equal(1, study.ToleranceConsumedCount);
            Assert.True(study.HasToleranceConsumingItem);
        }

        [Fact]
        public void WithoutToleranceTheShareIsUnknownNotZero()
        {
            RepeatabilityStudy study = RepeatabilityStudyBuilder.Build(
                [Sample("孔径", 10.0, 0), Sample("孔径", 11.0, 1)],
                unit: "mm",
                options: SameWorkpiece);

            RepeatabilityStudyItem bore = study.Items.Single();
            // 没有公差就没有"够不够稳"的答案——返回 null 而不是 false 或 0%。
            Assert.Null(bore.ToleranceBand);
            Assert.Null(bore.RepeatabilityShareOfTolerancePercent);
            Assert.Null(bore.RepeatabilityConsumesEntireTolerance);
            Assert.Equal(0, study.ToleranceConsumedCount);
        }

        [Fact]
        public void ZeroToleranceBandDoesNotProduceDivideByZeroShare()
        {
            RepeatabilityStudy study = RepeatabilityStudyBuilder.Build(
                [Sample("孔径", 10.0, 0), Sample("孔径", 11.0, 1)],
                toleranceLookup: _ => (10.0, 0, 0),
                unit: "mm",
                options: SameWorkpiece);

            RepeatabilityStudyItem bore = study.Items.Single();
            // 带宽为 0（即公差 ±0）不能算出无穷大的百分比。
            Assert.Null(bore.RepeatabilityShareOfTolerancePercent);
            Assert.Null(bore.RepeatabilityConsumesEntireTolerance);
        }

        [Fact]
        public void BlankKeysAndEmptyInputAreIgnoredGracefully()
        {
            RepeatabilityStudy empty = RepeatabilityStudyBuilder.Build([], unit: "mm");
            Assert.Empty(empty.Items);
            Assert.False(empty.HasConclusiveResult);

            RepeatabilityStudy blank = RepeatabilityStudyBuilder.Build(
                [Sample(" ", 1.0, 0), Sample("", 2.0, 1)],
                unit: "mm",
                options: SameWorkpiece);
            Assert.Empty(blank.Items);
        }

        [Fact]
        public void RenderMarkdownWarnsWhenToleranceIsConsumed()
        {
            RepeatabilityStudy study = RepeatabilityStudyBuilder.Build(
                [Sample("孔径", 10.0, 0), Sample("孔径", 10.6, 1), Sample("孔径", 9.4, 2)],
                toleranceLookup: _ => (10.0, 0.05, 0.05),
                quantityLookup: _ => "半径",
                unit: "mm",
                options: SameWorkpiece);

            string markdown = RepeatabilityStudyRenderer.RenderMarkdown(study);

            Assert.Contains("# 重复性研究报告", markdown);
            Assert.Contains("参与图像数: 3", markdown);
            Assert.Contains("占满公差带", markdown);
            Assert.Contains("| 孔径 | 半径 | mm | 3 |", markdown);
            Assert.Contains("n-1 分母", markdown);
        }

        [Fact]
        public void RenderMarkdownEscapesPipeInLabels()
        {
            RepeatabilityStudy study = RepeatabilityStudyBuilder.Build(
                [Sample("a|b", 1.0, 0), Sample("a|b", 2.0, 1)],
                unit: "mm");

            string markdown = RepeatabilityStudyRenderer.RenderMarkdown(study);
            Assert.Contains("a\\|b", markdown);
        }

        [Theory]
        [InlineData(RepeatabilityStudyContext.Unspecified)]
        [InlineData(RepeatabilityStudyContext.DifferentWorkpieces)]
        public void SourceVariationDoesNotClaimSameWorkpieceRepeatability(RepeatabilityStudyContext context)
        {
            RepeatabilityStudy study = RepeatabilityStudyBuilder.Build(
                [Sample("slot:0", 10.0, 0, "内径"), Sample("slot:0", 11.0, 1, "内径")],
                options: new RepeatabilityStudyOptions(context));

            RepeatabilityStudyItem item = Assert.Single(study.Items);
            Assert.Equal(2, item.Statistics.SampleCount);
            Assert.False(item.HasEnoughSamples);
            Assert.False(study.HasConclusiveResult);
            Assert.Null(item.RepeatabilityShareOfTolerancePercent);
            Assert.Contains("源间变化", RepeatabilityStudyRenderer.RenderMarkdown(study));
        }

        [Fact]
        public void SourceCountIncludesFailureSourcesAndDuplicateRowsFromOneSourceDoNotCreateRounds()
        {
            RepeatabilityStudy study = RepeatabilityStudyBuilder.Build(
                [Sample("slot:0", 10.0, 0), Sample("slot:0", 99.0, 0), Sample("slot:0", 11.0, 1)],
                options: SameWorkpiece,
                sourceIndices: [0, 1, 2]);

            Assert.Equal(3, study.SourceCount);
            Assert.Equal(2, Assert.Single(study.Items).Statistics.SampleCount);
        }

        [Fact]
        public void PerItemUnitLookupKeepsAngularAndLengthMeasurementsDistinct()
        {
            RepeatabilityStudy study = RepeatabilityStudyBuilder.Build(
                [Sample("angle", 1.0, 0), Sample("angle", 2.0, 1), Sample("length", 3.0, 0), Sample("length", 4.0, 1)],
                unit: "mm",
                options: SameWorkpiece,
                unitLookup: key => key == "angle" ? "°" : "mm");

            Assert.Equal("°", study.Items.Single(item => item.Label == "angle").Unit);
            Assert.Equal("mm", study.Items.Single(item => item.Label == "length").Unit);
            string markdown = RepeatabilityStudyRenderer.RenderMarkdown(study);
            Assert.Contains("| angle | angle | angle | ° | 2 |", markdown);
            Assert.Contains("| length | length | length | mm | 2 |", markdown);
        }
    }
}
