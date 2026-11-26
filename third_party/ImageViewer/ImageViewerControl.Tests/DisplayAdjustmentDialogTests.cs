using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using ImageViewer.Core.Display;
using ImageViewer.Dialogs;
using ImageViewer.Localization;
using Xunit;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// 显示调节对话框的交互测试：自动窗宽窗位、滑块/文本框提交与裁剪、反色与无图像提示。
    /// Chinese: 对话框只产出参数并回调预览，测试通过私有成员驱动真实的 UI 事件路径。
    /// English: Drives the real UI event paths of the display adjustment dialog through its private fields.
    /// </summary>
    [Collection(WpfTestCollection.Name)]
    [Trait("Category", "Wpf")]
    public class DisplayAdjustmentDialogTests
    {
        [Fact]
        public void ApplyAutoWindowLevel_OverridesWindowOnlyAndPreviews()
        {
            WpfTestRunner.Run(() =>
            {
                var initial = new DisplayAdjustment
                {
                    WindowWidth = 0.5,
                    WindowLevel = 0.4,
                    Brightness = 0.1,
                    Contrast = -0.2,
                    Gamma = 1.2,
                    Invert = true
                };
                var previews = new List<DisplayAdjustment>();
                var dialog = new DisplayAdjustmentDialog(
                    initial,
                    () => initial with { WindowWidth = 0.8, WindowLevel = 0.6 },
                    previews.Add);

                WpfTestRunner.InvokePrivate(dialog, "ApplyAutoWindowLevel");

                DisplayAdjustment last = Assert.Single(previews);
                Assert.Equal(0.8, last.WindowWidth, 6);
                Assert.Equal(0.6, last.WindowLevel, 6);
                Assert.Equal(0.1, last.Brightness, 6);
                Assert.Equal(-0.2, last.Contrast, 6);
                Assert.Equal(1.2, last.Gamma, 6);
                Assert.True(last.Invert);
                dialog.Close();
            });
        }

        [Fact]
        public void SliderAndTextCommit_UpdatePreviewAndClampInvalidInput()
        {
            WpfTestRunner.Run(() =>
            {
                var previews = new List<DisplayAdjustment>();
                var dialog = new DisplayAdjustmentDialog(DisplayAdjustment.Identity, autoProvider: null, previews.Add);
                object widthField = GetField(dialog, "_windowWidth")!;
                var slider = (Slider)GetField(widthField, "_slider")!;
                var textBox = (TextBox)GetField(widthField, "_textBox")!;

                slider.Value = 200;

                Assert.Equal("200", textBox.Text);
                Assert.NotEmpty(previews);
                Assert.Equal(200 / 255.0, previews[^1].WindowWidth, 6);

                textBox.Text = "999";
                WpfTestRunner.InvokePrivate(widthField, "CommitText");

                Assert.Equal(255, slider.Value, 6);
                Assert.Equal("255", textBox.Text);
                Assert.Equal(1.0, previews[^1].WindowWidth, 6);

                textBox.Text = "not-a-number";
                WpfTestRunner.InvokePrivate(widthField, "CommitText");

                Assert.Equal("255", textBox.Text);
                Assert.Equal(1.0, previews[^1].WindowWidth, 6);

                var invert = (CheckBox)GetField(dialog, "_invert")!;
                invert.IsChecked = false;

                Assert.False(previews[^1].Invert);

                WpfTestRunner.InvokePrivate(dialog, "Apply", DisplayAdjustment.Identity);

                DisplayAdjustment reset = previews[^1];
                Assert.Equal(1.0, reset.WindowWidth, 6);
                Assert.Equal(0, reset.Brightness);
                Assert.Equal(0, reset.Contrast);
                Assert.Equal(1.0, reset.Gamma, 6);
                Assert.False(reset.Invert);
                dialog.Close();
            });
        }

        [Fact]
        public void AutoWindowLevelWithoutProvider_ShowsMissingImageHint()
        {
            WpfTestRunner.Run(() =>
            {
                var dialog = new DisplayAdjustmentDialog(DisplayAdjustment.Identity);

                WpfTestRunner.InvokePrivate(dialog, "ApplyAutoWindowLevel");

                var errorText = (TextBlock)GetField(dialog, "_errorText")!;
                Assert.Equal(UiText.Get("DisplayAdjustmentAutoNeedsImage"), errorText.Text);

                object?[] arguments = [null];
                bool valid = (bool)WpfTestRunner.InvokePrivate(dialog, "TryBuild", arguments)!;
                Assert.True(valid);
                Assert.NotNull(arguments[0]);
                dialog.Close();
            });
        }

        private static object? GetField(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Field '{name}' was not found.");
            return field.GetValue(target);
        }
    }
}