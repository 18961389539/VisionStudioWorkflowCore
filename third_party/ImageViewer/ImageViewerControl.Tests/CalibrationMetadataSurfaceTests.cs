using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ImageViewer.Dialogs;
using ImageViewer.Localization;
using ImageViewer.Models;
using Xunit;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// ①-1 守卫：标定精度元数据必须可以从标定对话框进入、并在状态栏徽标上可见。
    /// Chinese: 这些字段此前只存在于 CameraCalibration 数据模型里，没有任何 UI 出口，
    /// 用户无法判断"重投影 RMS / 不确定度是否满足当前测量要求"。
    /// </summary>
    [Collection(WpfTestCollection.Name)]
    public class CalibrationMetadataSurfaceTests
    {
        [Fact]
        public void CalibrationDialog_ExposesExtendedDistortionAndAccuracyFields()
        {
            WpfTestRunner.Run(() =>
            {
                var dialog = new CalibrationDialog("mm");
                Realize(dialog);

                // 完整 Brown-Conrady 模型：K3 与切向畸变此前完全无法输入
                AssertFieldExists(dialog, "CalibrationDialogK3Label");
                AssertFieldExists(dialog, "CalibrationDialogP1Label");
                AssertFieldExists(dialog, "CalibrationDialogP2Label");

                // 精度元数据：重投影 RMS、不确定度、标定标识
                AssertFieldExists(dialog, "CalibrationDialogReprojectionErrorLabel");
                AssertFieldExists(dialog, "CalibrationDialogUncertaintyLabel");
                AssertFieldExists(dialog, "CalibrationDialogIdLabel");

                // 标定几何基准
                AssertFieldExists(dialog, "CalibrationDialogPrincipalXLabel");
                AssertFieldExists(dialog, "CalibrationDialogPrincipalYLabel");
                AssertFieldExists(dialog, "CalibrationDialogNormalizationRadiusLabel");
            });
        }

        [Fact]
        public void CalibrationDialog_PrefillsFromExistingCalibration()
        {
            WpfTestRunner.Run(() =>
            {
                var existing = new CameraCalibration
                {
                    K1 = 0.0123,
                    K2 = -0.0045,
                    K3 = 0.0007,
                    TangentialP1 = 0.00011,
                    TangentialP2 = -0.00013,
                    ReprojectionErrorRms = 0.0834,
                    MeasurementUncertaintyPixels = 0.25,
                    CalibrationId = "CERT-2026-001"
                };

                var dialog = new CalibrationDialog("mm", existing);
                Realize(dialog);

                // 重新提交不应丢失任何既有元数据（"打开对话框点了确定就掉精度信息"是最容易出的回归）。
                Assert.True(dialog.TryCommit());
                Assert.NotNull(dialog.Distortion);
                Assert.Equal(0.0123, dialog.Distortion!.K1, 9);
                Assert.Equal(-0.0045, dialog.Distortion.K2, 9);
                Assert.Equal(0.0007, dialog.Distortion.K3, 9);
                Assert.Equal(0.00011, dialog.Distortion.TangentialP1, 9);
                Assert.Equal(-0.00013, dialog.Distortion.TangentialP2, 9);
                Assert.Equal(0.0834, dialog.Distortion.ReprojectionErrorRms, 9);
                Assert.Equal(0.25, dialog.Distortion.MeasurementUncertaintyPixels, 9);
                Assert.Equal("CERT-2026-001", dialog.Distortion.CalibrationId);
            });
        }

        [Fact]
        public void CalibrationDialogResult_ReportsDistortionAndForwardedK1K2()
        {
            var distortion = new CameraCalibration
            {
                K1 = 0.01,
                K2 = -0.002,
                K3 = 0.0003,
                TangentialP1 = 0.00004,
                TangentialP2 = -0.00005,
                ReprojectionErrorRms = 0.12,
                MeasurementUncertaintyPixels = 0.4,
                CalibrationId = "CERT-A"
            };

            var result = new CalibrationDialogResult(25d, "mm", distortion);

            // 旧调用方言（K1/K2 标量）必须继续可用，否则会破坏既有宿主与插件。
            Assert.Equal(0.01, result.K1, 9);
            Assert.Equal(-0.002, result.K2, 9);
            Assert.Same(distortion, result.Distortion);
        }

        [Fact]
        public void CalibrationDialogResult_WithoutDistortion_ReportsZeroK1K2()
        {
            var result = new CalibrationDialogResult(25d, "mm", null);

            Assert.Equal(0, result.K1);
            Assert.Equal(0, result.K2);
            Assert.Null(result.Distortion);
        }

        /// <summary>
        /// 精度元数据必须能穿过对话框进入宿主，而非在对话框里被丢弃。
        /// Chinese: 通过 <c>TryCommit</c> 驱动真实解析/校验路径（OnOkClick 只是"提示 + 关闭"外壳），
        /// 输入框按标签文本定位，避免依赖遍历顺序。
        /// </summary>
        [Fact]
        public void CalibrationDialog_Commit_CarriesAccuracyMetadataIntoDistortion()
        {
            WpfTestRunner.Run(() =>
            {
                var dialog = new CalibrationDialog("mm");
                Realize(dialog);
                SetFieldByLabel(dialog, "CalibrationDialogActualLengthLabel", "12.5");
                SetFieldByLabel(dialog, "CalibrationDialogK1Label", "0.01");
                SetFieldByLabel(dialog, "CalibrationDialogK2Label", "-0.002");
                SetFieldByLabel(dialog, "CalibrationDialogK3Label", "0.0003");
                SetFieldByLabel(dialog, "CalibrationDialogP1Label", "0.00004");
                SetFieldByLabel(dialog, "CalibrationDialogReprojectionErrorLabel", "0.07");
                SetFieldByLabel(dialog, "CalibrationDialogUncertaintyLabel", "0.3");
                SetFieldByLabel(dialog, "CalibrationDialogIdLabel", "CERT-B");

                Assert.True(dialog.TryCommit());

                Assert.Equal(12.5, dialog.Length, 6);
                Assert.Equal("mm", dialog.Unit);
                Assert.NotNull(dialog.Distortion);
                Assert.Equal(0.0003, dialog.Distortion!.K3, 9);
                Assert.Equal(0.00004, dialog.Distortion.TangentialP1, 9);
                Assert.Equal(0.07, dialog.Distortion.ReprojectionErrorRms, 9);
                Assert.Equal(0.3, dialog.Distortion.MeasurementUncertaintyPixels, 9);
                Assert.Equal("CERT-B", dialog.Distortion.CalibrationId);
                Assert.NotNull(dialog.Distortion.CalibratedAtUtc);
                Assert.True(dialog.Distortion.HasExtendedDistortionModel);
            });
        }

        /// <summary>
        /// 全零输入 + 无精度元数据时必须返回 null，避免生成"空标定"把状态栏点亮。
        /// </summary>
        [Fact]
        public void CalibrationDialog_Commit_WithoutDistortionOrAccuracy_ProducesNullDistortion()
        {
            WpfTestRunner.Run(() =>
            {
                var dialog = new CalibrationDialog("px");
                Realize(dialog);
                SetFieldByLabel(dialog, "CalibrationDialogActualLengthLabel", "10");

                Assert.True(dialog.TryCommit());

                Assert.Equal("mm", dialog.Unit);
                Assert.Null(dialog.Distortion);
                Assert.Equal(0, dialog.K1);
                Assert.Equal(0, dialog.K2);
            });
        }

        /// <summary>非法长度必须被拒绝，且不得写入任何状态。</summary>
        [Fact]
        public void CalibrationDialog_Commit_RejectsInvalidLength()
        {
            WpfTestRunner.Run(() =>
            {
                var dialog = new CalibrationDialog("mm");
                Realize(dialog);
                SetFieldByLabel(dialog, "CalibrationDialogActualLengthLabel", "0");

                Assert.False(dialog.TryCommit());
                Assert.Null(dialog.Distortion);
            });
        }

        /// <summary>
        /// 只有精度元数据、没有畸变系数时也必须保留。
        /// Chinese: "已知长度 → 比例" 与 "畸变标定" 是两条独立的证据，前者可以有精度记录而无畸变系数。
        /// </summary>
        [Fact]
        public void CalibrationDialog_Commit_KeepsAccuracyMetadataWithoutDistortion()
        {
            WpfTestRunner.Run(() =>
            {
                var dialog = new CalibrationDialog("mm");
                Realize(dialog);
                SetFieldByLabel(dialog, "CalibrationDialogActualLengthLabel", "10");
                SetFieldByLabel(dialog, "CalibrationDialogUncertaintyLabel", "0.15");

                Assert.True(dialog.TryCommit());

                Assert.NotNull(dialog.Distortion);
                Assert.False(dialog.Distortion!.IsEnabled);
                Assert.Equal(0.15, dialog.Distortion.MeasurementUncertaintyPixels, 9);
            });
        }

        /// <summary>
        /// 强制完成布局，让 ScrollViewer.Content 进入逻辑树。
        /// Chinese: Window 未显示时没有可视树，但逻辑树是完整的，因此遍历走 LogicalTreeHelper。
        /// </summary>
        private static void Realize(Window window)
        {
            window.Measure(new Size(600, 900));
            window.Arrange(new Rect(0, 0, 600, 900));
            window.UpdateLayout();
        }

        private static void AssertFieldExists(CalibrationDialog dialog, string labelKey)
        {
            string label = UiText.Get(labelKey);
            List<DependencyObject> nodes = [];
            CollectDescendants(dialog, nodes);

            bool found = false;
            for (int i = 0; i < nodes.Count - 1 && !found; i++)
            {
                found = nodes[i] is TextBlock { Text: var value }
                    && string.Equals(value, label, StringComparison.Ordinal)
                    && nodes[i + 1] is TextBox;
            }

            Assert.True(found, $"未找到标签「{label}」对应的输入框。");
        }

        /// <summary>
        /// 按标签文本定位输入框并写入文本。
        /// Chinese: 依赖"标签 TextBlock 紧随其后是 TextBox"的对话框构型，避免按下标定位在插入新字段时脆断。
        /// </summary>
        private static void SetFieldByLabel(CalibrationDialog dialog, string labelKey, string text)
        {
            string label = UiText.Get(labelKey);
            List<DependencyObject> nodes = [];
            CollectDescendants(dialog, nodes);

            for (int i = 0; i < nodes.Count - 1; i++)
            {
                if (nodes[i] is TextBlock { Text: var value } && string.Equals(value, label, StringComparison.Ordinal)
                    && nodes[i + 1] is TextBox textBox)
                {
                    textBox.Text = text;
                    return;
                }
            }

            Assert.Fail($"未找到标签「{label}」对应的输入框。");
        }

        private static void CollectDescendants(DependencyObject root, List<DependencyObject> nodes)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is DependencyObject node)
                {
                    nodes.Add(node);
                    CollectDescendants(node, nodes);
                }
            }
        }
    }
}
