using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Abstractions;
using ImageViewer.Controls;
using ImageViewer.Core.Display;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// 显示参数与"未保存"状态的关系。
    /// Chinese: <see cref="DisplayAdjustment"/> 会随会话保存并在打开时恢复，因此它的变化必须让文档变脏；
    /// 伪彩、直方图、剖面属于纯浏览状态，不入会话，改它们不能把文档标脏（否则用户会为一个存不下来的
    /// 视觉偏好被迫保存）。两条是相反方向的断言，任一侧写错都会让用户的实际体验反过来。
    /// </summary>
    [Collection(WpfTestCollection.Name)]
    [Trait("Category", "Integration")]
    [Trait("Category", "Wpf")]
    public sealed class DisplayDirtyStateTests
    {
        [Fact]
        public void ChangingDisplayAdjustment_MarksDocumentDirty()
        {
            WpfTestRunner.Run(() =>
            {
                using ImageViewerHost host = CreateHost();
                using ImageViewer.Controls.ImageViewer viewer = host.CreateViewer();
                viewer.ImageSource = CreateBitmap(40);
                viewer.MarkDocumentClean();
                Assert.False(viewer.IsDirty);

                viewer.DisplayAdjustment = new DisplayAdjustment { Brightness = 0.2, Gamma = 1.4 };

                Assert.True(viewer.IsDirty);
            });
        }

        [Fact]
        public void ChangingPseudoColorPalette_DoesNotMarkDocumentDirty()
        {
            WpfTestRunner.Run(() =>
            {
                using ImageViewerHost host = CreateHost();
                using ImageViewer.Controls.ImageViewer viewer = host.CreateViewer();
                viewer.ImageSource = CreateBitmap(40);
                viewer.MarkDocumentClean();

                viewer.PseudoColorPalette = PseudoColorPalette.Jet;

                Assert.False(viewer.IsDirty);
            });
        }

        [Fact]
        public void ReopeningSessionWithDisplayAdjustment_RestoresItAndStaysClean()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), $"iv-display-{Guid.NewGuid():N}");
                Directory.CreateDirectory(root);
                try
                {
                    string imagePath = Path.Combine(root, "source.png");
                    SaveBitmap(imagePath, CreateBitmap(40));
                    string sessionPath = Path.Combine(root, "display.ivsession");

                    var registry = RoiPluginRegistry.CreateBuiltIn();
                    var service = new ImageViewerSessionService();
                    await service.SaveToFileAsync(
                        sessionPath,
                        new ImageViewerPersistenceSnapshot(
                            imagePath,
                            RoiPersistenceService.CreateDocument([], 1, "px", registry),
                            1,
                            0,
                            0,
                            null)
                        {
                            Display = new DisplayAdjustment { WindowWidth = 0.5, WindowLevel = 0.4, Invert = true }
                        },
                        registry);

                    using ImageViewerHost host = CreateHost(new OpenTargets(sessionPath));
                    using ImageViewer.Controls.ImageViewer viewer = host.CreateViewer();
                    Assert.True(await viewer.OpenProjectFromFileAsync(sessionPath));

                    Assert.Equal(0.5, viewer.DisplayAdjustment.WindowWidth);
                    Assert.Equal(0.4, viewer.DisplayAdjustment.WindowLevel);
                    Assert.True(viewer.DisplayAdjustment.Invert);
                    // 加载会话时先应用显示参数再显式 MarkClean，恢复的显示参数不能把刚打开的会话标脏。
                    Assert.False(viewer.IsDirty);
                }
                finally
                {
                    Directory.Delete(root, recursive: true);
                }
            });
        }

        private static ImageViewerHost CreateHost(IImageViewerFileDialogService? fileDialogService = null)
        {
            var runtime = ImageViewerTestServices.CreateRuntimeServices(fileDialogService: fileDialogService);
            return ImageViewerTestServices.CreateHost(
                RoiPluginRegistry.CreateBuiltIn(), runtime, ImageViewerHostDefaults.CreateHostServices());
        }

        private static BitmapSource CreateBitmap(byte value)
        {
            return BitmapSource.Create(
                2, 2, 96, 96, PixelFormats.Gray8, null,
                new[] { value, value, value, value }, 2);
        }

        private static void SaveBitmap(string path, BitmapSource bitmap)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream stream = File.Create(path);
            encoder.Save(stream);
        }

        private sealed class OpenTargets(string session) : IImageViewerFileDialogService
        {
            public string? ShowOpenImageDialog(Window? owner) => null;
            public string[] ShowOpenImageFilesDialog(Window? owner) => [];
            public string? ShowSaveRoiDialog(Window? owner) => null;
            public string? ShowOpenRoiDialog(Window? owner) => null;
            public string? ShowSaveSessionDialog(Window? owner) => null;
            public string? ShowOpenSessionDialog(Window? owner) => session;
            public string? ShowSaveSnapshotDialog(Window? owner) => null;
            public string? ShowSaveAnalysisCsvDialog(Window? owner) => null;
            public string? ShowSaveXpsDialog(Window? owner) => null;
            public string? ShowSaveProjectPackageDialog(Window? owner) => null;
            public string? ShowSaveRecipeDialog(Window? owner) => null;
            public string? ShowOpenRecipeDialog(Window? owner) => null;
        }
    }
}
