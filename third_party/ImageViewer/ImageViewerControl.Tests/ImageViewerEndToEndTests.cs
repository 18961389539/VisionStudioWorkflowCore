using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Abstractions;
using ImageViewer.Controls;
using ImageViewer.Core.Geometry;
using ImageViewer.Core.Persistence;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests
{
    [Collection(WpfTestCollection.Name)]
    [Trait("Category", "E2E")]
    [Trait("Category", "Wpf")]
    public sealed class ImageViewerEndToEndTests
    {
        [Fact]
        public void InvalidToleranceDraft_SurvivesReselectionAndBlocksCsvUntilCorrected()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                string csvPath = Path.Combine(Path.GetTempPath(), $"iv-tolerance-{Guid.NewGuid():N}.csv");
                try
                {
                    int saveDialogs = 0;
                    var dialogs = new SaveTargets("", "") { SelectAnalysisCsv = () => { saveDialogs++; return csvPath; } };
                    using var runtime = ImageViewerTestServices.CreateRuntimeServices(fileDialogService: dialogs);
                    using var host = ImageViewerTestServices.CreateHost(
                        RoiPluginRegistry.CreateBuiltIn(), runtime, ImageViewerHostDefaults.CreateHostServices());
                    using var viewer = host.CreateViewer();
                    var line = new LineMeasureRoi
                    {
                        Label = "检查尺寸",
                        P1 = new PointD(0, 0),
                        P2 = new PointD(10, 0),
                        Tolerance = new MeasurementTolerance { Nominal = 10, TolerancePlus = 0.5 }
                    };
                    Assert.True(viewer.AddRoi(line));
                    var editorHost = Assert.IsType<ContentControl>(viewer.FindName("roiPropertyEditorHost"));
                    TextBox PlusField() => Assert.Single(Assert.IsType<StackPanel>(editorHost.Content).Children.OfType<TextBox>()
                        .Where(field => System.Windows.Automation.AutomationProperties.GetName(field) == UiText.Get("EditorTolerancePlus")));

                    PlusField().Text = "-2";
                    Assert.Equal(0.5, line.Tolerance?.TolerancePlus);
                    viewer.ViewerState.SelectedRoi = null;
                    await viewer._controlComposition.FeatureMenuCommandController.ExecuteAsync(ImageViewerFeatureMenuCommand.ExportAnalysisCsv);
                    Assert.Equal(0, saveDialogs);
                    Assert.False(File.Exists(csvPath));
                    Assert.Contains(line.Label, viewer.statusHintTextBlock.Text);

                    viewer.ViewerState.SelectedRoi = line;
                    Assert.Equal("-2", PlusField().Text);
                    Assert.Contains(UiText.Get("EditorToleranceInvalidLimit"), System.Windows.Automation.AutomationProperties.GetHelpText(PlusField()));
                    PlusField().Text = "0.25";
                    Assert.Equal(0.25, line.Tolerance?.TolerancePlus);
                    await viewer._controlComposition.FeatureMenuCommandController.ExecuteAsync(ImageViewerFeatureMenuCommand.ExportAnalysisCsv);
                    Assert.Equal(1, saveDialogs);
                    Assert.Contains(",\"Passed\",10,10,0.25,", await File.ReadAllTextAsync(csvPath));
                }
                finally
                {
                    File.Delete(csvPath);
                    File.Delete(Path.ChangeExtension(csvPath, ".metadata.json"));
                    File.Delete(Path.ChangeExtension(csvPath, ".report.md"));
                }
            });
        }

        [Fact]
        public void AxialOpenImageDialog_UsesTheAdaptiveHostWindowAndReplacesTheVolume()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                string path = Path.Combine(Path.GetTempPath(), $"iv-axial-open-{Guid.NewGuid():N}.png");
                Window? dialogOwner = null;
                var dialogs = new SaveTargets("", "") { SelectImage = owner => { dialogOwner = owner; return path; } };
                using var runtime = ImageViewerTestServices.CreateRuntimeServices(fileDialogService: dialogs);
                using var host = ImageViewerTestServices.CreateHost(
                    RoiPluginRegistry.CreateBuiltIn(), runtime, ImageViewerHostDefaults.CreateHostServices());
                var volume = new VolumeData([CreateBitmap(16, 16, 50), CreateBitmap(16, 16, 60)]);
                await using var viewer = new AdaptiveImageViewer(host.CreateViewer)
                {
                    Volume = volume,
                    ImageSource = volume.GetAxialSlice(0),
                    DisplayMode = AdaptiveDisplayMode.AxialSlice
                };
                var window = new Window { Width = 800, Height = 600, Content = viewer };
                try
                {
                    SaveBitmap(path, CreateBitmap(8, 6, 25));
                    window.Show();
                    WpfTestRunner.DrainDispatcher();

                    await viewer.VolumeViewer.SliceViewer.ShowOpenImageDialogAsync();
                    Assert.Same(window, dialogOwner);
                    Assert.Null(viewer.Volume);
                    Assert.Same(viewer.ImageViewer, viewer.ActiveView);
                    Assert.Equal(8, Assert.IsAssignableFrom<BitmapSource>(viewer.ImageSource).PixelWidth);
                }
                finally
                {
                    window.Close();
                    File.Delete(path);
                }
            });
        }

        [Fact]
        public void LargeImageWorkflow_LoadsPyramidAndUsesTiledViewport()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var window = new Window
                {
                    Width = 640,
                    Height = 480,
                    Content = viewer
                };

                try
                {
                    window.Show();
                    WpfTestRunner.DrainDispatcher();
                    window.UpdateLayout();

                    BitmapSource image = CreateBitmap(2001, 2001, 127);
                    viewer.SetImage(image);
                    Task prepare = Assert.IsAssignableFrom<Task>(WpfTestRunner.InvokePrivate(viewer, "PrepareAnalysisResourcesAsync", image));
                    await prepare;
                    WpfTestRunner.DrainDispatcher();

                    Assert.True(viewer.RuntimeOptions.EnableImagePyramid);
                    Assert.True(viewer.RuntimeOptions.EnableTiledRendering);
                    Assert.True(viewer.RuntimeOptions.AutoSelectPyramidLevel);
                    Assert.True(viewer._analysisState.PyramidLevels.Count > 1);
                    Assert.True(viewer._analysisState.LastRenderFrame.IsTiled);
                    Assert.InRange(viewer._analysisState.LastRenderFrame.Width, 1, 2001);
                    Assert.InRange(viewer._analysisState.LastRenderFrame.ScaleFactor, 0.0, 1.0);

                    viewer.RotateImageRight();
                    viewer.FlipImageHorizontal();
                    window.UpdateLayout();
                    Rect visible = viewer.rootGrid.TransformToVisual(viewer.imageContainer).TransformBounds(new Rect(viewer.rootGrid.RenderSize));
                    visible.Intersect(new Rect(0, 0, 2001, 2001));
                    Assert.False(visible.IsEmpty);
                    var frame = viewer._analysisState.LastRenderFrame;
                    var rendered = new Rect(frame.Left, frame.Top, frame.Width, frame.Height);
                    rendered.Inflate(1, 1);
                    Assert.True(rendered.Contains(visible), $"Rendered tile {rendered} should cover visible image {visible}.");
                }
                finally
                {
                    window.Close();
                    WpfTestRunner.DrainDispatcher();
                }
            });
        }

        [Fact]
        public void AnalysisExportWorkflow_WritesSourceFingerprintAndRoiInputs()
        {
            WpfTestRunner.Run(() =>
            {
                string root = CreateTempRoot();
                try
                {
                    string sourcePath = Path.Combine(root, "source.png");
                    string csvPath = Path.Combine(root, "analysis.csv");
                    BitmapSource image = CreateBitmap(32, 24, 64);
                    SaveBitmap(sourcePath, image);
                    image = ImageViewerImageDecoder.LoadDocumentImageAsync(sourcePath, null, null, CancellationToken.None).GetAwaiter().GetResult();
                    var roi = new CircleRoi
                    {
                        Center = new PointD(12, 10),
                        Radius = 4,
                        Label = "reference"
                    };
                    var settings = new Dictionary<string, string>
                    {
                        ["enableTiledRendering"] = "True",
                        ["pseudoColorPalette"] = "None"
                    };

                    RoiAnalysisExportService.SaveCsvAsync(
                        csvPath,
                        [roi],
                        image,
                        0.25,
                        "mm",
                        calibration: null,
                        exportContext: new RoiAnalysisExportContext(sourcePath, RoiPluginRegistry.CreateBuiltIn(), settings))
                        .GetAwaiter()
                        .GetResult();

                    string metadataPath = Path.ChangeExtension(csvPath, ".metadata.json");
                    Assert.True(File.Exists(csvPath));
                    Assert.True(File.Exists(metadataPath));
                    Assert.Contains("Type,Label", File.ReadAllText(csvPath));

                    using JsonDocument metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
                    JsonElement rootElement = metadata.RootElement;
                    Assert.Equal(2, rootElement.GetProperty("exportFormatVersion").GetInt32());
                    Assert.Equal(1, rootElement.GetProperty("roiCount").GetInt32());
                    Assert.Equal("mm", rootElement.GetProperty("calibration").GetProperty("physicalUnit").GetString());
                    Assert.Equal("True", rootElement.GetProperty("renderSettings").GetProperty("enableTiledRendering").GetString());
                    Assert.Equal(1, rootElement.GetProperty("roiDocument").GetProperty("Items").GetArrayLength());

                    string expectedSourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath))).ToLowerInvariant();
                    Assert.Equal(expectedSourceHash, rootElement.GetProperty("source").GetProperty("sha256").GetString());
                    Assert.False(string.IsNullOrWhiteSpace(rootElement.GetProperty("result").GetProperty("csvSha256").GetString()));
                }
                finally
                {
                    Directory.Delete(root, recursive: true);
                }
            });
        }

        [Fact]
        public void RecoveryWorkflow_RestoresAutosavedRoiAndKeepsDocumentDirty()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                string root = CreateTempRoot();
                var policy = new LocalAppDataImageViewerSessionStoragePolicy(root, TimeSpan.FromMinutes(5));
                RoiPluginRegistry registry = RoiPluginRegistry.CreateBuiltIn();
                var recoveredRoi = new CircleRoi
                {
                    Center = new PointD(8, 8),
                    Radius = 3,
                    Label = "recovered"
                };
                Directory.CreateDirectory(policy.AutoSaveDirectory);
                string recoveryPath = Path.Combine(policy.AutoSaveDirectory, "autosave.ivsession");
                new ImageViewerSessionService().SaveToFile(
                    recoveryPath,
                    new ImageViewerPersistenceSnapshot(
                        null,
                        RoiPersistenceService.CreateDocument([recoveredRoi], 0.5, "mm", registry),
                        1.25,
                        4,
                        6,
                        null),
                    registry);

                var defaultHostServices = ImageViewerHostDefaults.CreateHostServices();
                var hostServices = new ImageViewerHostServices(
                    defaultHostServices.DispatcherTimerFactory,
                    defaultHostServices.RefreshSchedulerFactory,
                    defaultHostServices.LatestTaskSchedulerFactory,
                    defaultHostServices.PeriodicTaskSchedulerFactory,
                    defaultHostServices.AnalysisDiagnostics,
                    policy);
                using var runtimeServices = ImageViewerTestServices.CreateRuntimeServices(sessionStoragePolicy: policy);
                using var host = ImageViewerTestServices.CreateHost(registry, runtimeServices, hostServices);
                using var viewer = host.CreateViewer();

                try
                {
                    Assert.True(viewer.HasRecoverySnapshot);

                    await viewer._controlComposition.SessionController.RecoverLatestAutoSaveAsync();

                    Assert.False(viewer.HasRecoverySnapshot);
                    Assert.True(viewer.IsDirty);
                    Assert.Single(viewer.ViewerState.AllRois);
                    Assert.Equal("recovered", viewer.ViewerState.AllRois.Single().Label);
                    Assert.Equal(0.5, viewer.PixelSize);
                    Assert.Equal("mm", viewer.PhysicalUnit);
                }
                finally
                {
                    Directory.Delete(root, recursive: true);
                }
            });
        }

        [Fact]
        public void SessionWorkflow_RelinksMovedImageAndRemembersNewPathAfterSave()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                string root = CreateTempRoot();
                try
                {
                    var policy = new LocalAppDataImageViewerSessionStoragePolicy(root, TimeSpan.FromMinutes(5));
                    var registry = RoiPluginRegistry.CreateBuiltIn();
                    var service = new ImageViewerSessionService();
                    string sessionPath = Path.Combine(root, "project.ivsession");
                    string originalPath = Path.Combine(root, "original.png");
                    string movedPath = Path.Combine(root, "moved.png");
                    string wrongPath = Path.Combine(root, "wrong.png");
                    ushort[] samples = [4, 1000, 32768, 65500];
                    SaveBitmap(originalPath, CreateGray16(samples));
                    SaveBitmap(wrongPath, CreateGray16([4, 1000, 32768, 65501]));
                    await service.SaveToFileAsync(sessionPath, CreateRoiSession(originalPath, registry), registry);
                    File.Move(originalPath, movedPath);
                    string? savedSha = (await service.LoadFromFileAsync(sessionPath, registry)).ImageSha256;
                    Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(movedPath))), savedSha);
                    int pickerCalls = 0;
                    var dialogs = new SaveTargets(sessionPath, "")
                    {
                        LocateSessionImage = (image, session) =>
                        {
                            pickerCalls++;
                            Assert.Equal(originalPath, image);
                            Assert.Equal(sessionPath, session);
                            return pickerCalls == 1 ? wrongPath : movedPath;
                        }
                    };
                    using var runtime = ImageViewerTestServices.CreateRuntimeServices(sessionStoragePolicy: policy, fileDialogService: dialogs);
                    using var host = ImageViewerTestServices.CreateHost(registry, runtime, CreateRecoveryHostServices(policy));
                    using var viewer = host.CreateViewer();

                    Assert.False(await viewer.OpenProjectFromFileAsync(sessionPath));
                    Assert.Null(viewer.ImageSource);
                    Assert.Empty(viewer.ViewerState.AllRois);
                    Assert.True(await viewer.OpenProjectFromFileAsync(sessionPath));
                    Assert.True(viewer.IsDirty);
                    Assert.Equal("persisted", Assert.Single(viewer.ViewerState.AllRois).Label);
                    AssertGray16Samples(viewer.ImageSource, samples);
                    Assert.Equal(originalPath, (await service.LoadFromFileAsync(sessionPath, registry)).ImagePath);

                    await viewer.SaveSessionAsync();

                    Assert.False(viewer.IsDirty);
                    Assert.Equal(movedPath, (await service.LoadFromFileAsync(sessionPath, registry)).ImagePath);
                    Assert.True(await viewer.OpenProjectFromFileAsync(sessionPath));
                    Assert.Equal(2, pickerCalls);
                    Assert.Equal(savedSha, (await service.LoadFromFileAsync(sessionPath, registry)).ImageSha256);
                }
                finally
                {
                    Directory.Delete(root, recursive: true);
                }
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SessionWorkflow_CancelledOrUnreadableReplacementPreservesDirtyDocument(bool chooseUnreadableImage)
        {
            WpfTestRunner.RunAsync(async () =>
            {
                string root = CreateTempRoot();
                try
                {
                    var policy = new LocalAppDataImageViewerSessionStoragePolicy(root, TimeSpan.FromMinutes(5));
                    var registry = RoiPluginRegistry.CreateBuiltIn();
                    string sessionPath = Path.Combine(root, "project.ivsession");
                    string unreadablePath = Path.Combine(root, "corrupt.png");
                    File.WriteAllText(unreadablePath, "This is not a PNG.");
                    await new ImageViewerSessionService().SaveToFileAsync(sessionPath,
                        CreateRoiSession(Path.Combine(root, "missing.png"), registry), registry);
                    int pickerCalls = 0;
                    var dialogs = new SaveTargets("", "")
                    {
                        LocateSessionImage = (_, _) =>
                        {
                            pickerCalls++;
                            return chooseUnreadableImage ? unreadablePath : null;
                        }
                    };
                    using var runtime = ImageViewerTestServices.CreateRuntimeServices(sessionStoragePolicy: policy, fileDialogService: dialogs);
                    using var host = ImageViewerTestServices.CreateHost(registry, runtime, CreateRecoveryHostServices(policy));
                    using var viewer = host.CreateViewer();
                    BitmapSource original = CreateBitmap(2, 2, 42);
                    var roi = new CircleRoi { Center = new(1, 1), Radius = 1 };
                    viewer.SetImage(original);
                    viewer.ViewerState.ReplaceAllRois([roi]);
                    viewer._controlComposition.SessionController.MarkDirty();

                    Assert.False(await viewer.OpenProjectFromFileAsync(sessionPath));

                    Assert.Equal(1, pickerCalls);
                    Assert.Same(original, viewer.ImageSource);
                    Assert.Same(roi, Assert.Single(viewer.ViewerState.AllRois));
                    Assert.True(viewer.IsDirty);
                    Assert.Equal(chooseUnreadableImage, viewer.HasDiagnosticError);
                }
                finally
                {
                    Directory.Delete(root, recursive: true);
                }
            });
        }

        [Fact]
        public void SessionWorkflow_OverwrittenImageIsRejectedAndCannotReplaceExistingSaves()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                string root = CreateTempRoot();
                try
                {
                    var policy = new LocalAppDataImageViewerSessionStoragePolicy(root, TimeSpan.FromMinutes(5));
                    var registry = RoiPluginRegistry.CreateBuiltIn();
                    var service = new ImageViewerSessionService();
                    string sessionPath = Path.Combine(root, "project.ivsession");
                    string packagePath = Path.Combine(root, "project.ivpkg");
                    string imagePath = Path.Combine(root, "source.png");
                    ushort[] original = [1, 2, 30000, 65530];
                    SaveBitmap(imagePath, CreateGray16(original));
                    await service.SaveToFileAsync(sessionPath, CreateRoiSession(imagePath, registry), registry);
                    using var runtime = ImageViewerTestServices.CreateRuntimeServices(sessionStoragePolicy: policy, fileDialogService: new SaveTargets(sessionPath, packagePath));
                    using var host = ImageViewerTestServices.CreateHost(registry, runtime, CreateRecoveryHostServices(policy));
                    using var viewer = host.CreateViewer();

                    Assert.True(await viewer.OpenProjectFromFileAsync(sessionPath));
                    await viewer._controlComposition.SessionController.ExportProjectPackageAsync();
                    byte[] savedSession = File.ReadAllBytes(sessionPath);
                    byte[] savedPackage = File.ReadAllBytes(packagePath);
                    SaveBitmap(imagePath, CreateGray16([1, 2, 30001, 65530]));
                    viewer._controlComposition.SessionController.MarkDirty();

                    await viewer.SaveSessionAsync();
                    await viewer._controlComposition.SessionController.ExportProjectPackageAsync();

                    Assert.True(viewer.IsDirty);
                    Assert.Equal(savedSession, File.ReadAllBytes(sessionPath));
                    Assert.Equal(savedPackage, File.ReadAllBytes(packagePath));
                    AssertGray16Samples(viewer.ImageSource, original);
                    using var reader = host.CreateViewer();
                    Assert.False(await reader.OpenProjectFromFileAsync(sessionPath));
                    Assert.True(await reader.OpenProjectFromFileAsync(packagePath));
                    AssertGray16Samples(reader.ImageSource, original);
                }
                finally
                {
                    Directory.Delete(root, recursive: true);
                }
            });
        }

        [Fact]
        public void SessionWorkflow_RoiOnlySessionClearsPreviousImageAndSavesWithoutOne()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                string root = CreateTempRoot();
                try
                {
                    var policy = new LocalAppDataImageViewerSessionStoragePolicy(root, TimeSpan.FromMinutes(5));
                    var registry = RoiPluginRegistry.CreateBuiltIn();
                    var service = new ImageViewerSessionService();
                    string initialPath = Path.Combine(root, "image.ivsession");
                    string roiOnlyPath = Path.Combine(root, "roi-only.ivsession");
                    string imagePath = Path.Combine(root, "source.png");
                    SaveBitmap(imagePath, CreateBitmap(2, 2, 42));
                    await service.SaveToFileAsync(initialPath, CreateRoiSession(imagePath, registry), registry);
                    await service.SaveToFileAsync(roiOnlyPath, CreateRoiSession(null, registry), registry);
                    using var runtime = ImageViewerTestServices.CreateRuntimeServices(sessionStoragePolicy: policy, fileDialogService: new SaveTargets(roiOnlyPath, ""));
                    using var host = ImageViewerTestServices.CreateHost(registry, runtime, CreateRecoveryHostServices(policy));
                    using var viewer = host.CreateViewer();

                    Assert.True(await viewer.OpenProjectFromFileAsync(initialPath));
                    Assert.NotNull(viewer.ImageSource);
                    Assert.True(await viewer.OpenProjectFromFileAsync(roiOnlyPath));
                    Assert.Null(viewer.ImageSource);
                    Assert.Equal("persisted", Assert.Single(viewer.ViewerState.AllRois).Label);
                    Assert.False(viewer.IsDirty);

                    await viewer.SaveSessionAsync();
                    Assert.Null((await service.LoadFromFileAsync(roiOnlyPath, registry)).ImagePath);
                }
                finally
                {
                    Directory.Delete(root, recursive: true);
                }
            });
        }

        [Fact]
        public void RecoveryWorkflow_UserCanRelinkMissingImageFromSelectedSnapshot()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                string root = CreateTempRoot();
                try
                {
                    var policy = new LocalAppDataImageViewerSessionStoragePolicy(root, TimeSpan.FromMinutes(5));
                    var registry = RoiPluginRegistry.CreateBuiltIn();
                    Directory.CreateDirectory(policy.AutoSaveDirectory);
                    string snapshotPath = Path.Combine(policy.AutoSaveDirectory, "autosave.ivsession");
                    string missingPath = Path.Combine(root, "original.png");
                    string relocatedPath = Path.Combine(root, "relocated.png");
                    ushort[] samples = [1, 2, 8000, 40000];
                    SaveBitmap(missingPath, CreateGray16(samples));
                    await new ImageViewerSessionService().SaveToFileAsync(snapshotPath, CreateRoiSession(missingPath, registry), registry);
                    File.Move(missingPath, relocatedPath);
                    var dialogs = new SaveTargets("", "")
                    {
                        LocateSessionImage = (image, session) =>
                        {
                            Assert.Equal(missingPath, image);
                            Assert.Equal(snapshotPath, session);
                            return relocatedPath;
                        }
                    };
                    using var runtime = ImageViewerTestServices.CreateRuntimeServices(sessionStoragePolicy: policy, fileDialogService: dialogs);
                    using var host = ImageViewerTestServices.CreateHost(registry, runtime, CreateRecoveryHostServices(policy));
                    using var viewer = host.CreateViewer();

                    await viewer._controlComposition.SessionController.RecoverAutoSaveAsync(candidates => Assert.Single(candidates));

                    Assert.True(viewer.IsDirty);
                    Assert.Equal("persisted", Assert.Single(viewer.ViewerState.AllRois).Label);
                    AssertGray16Samples(viewer.ImageSource, samples);
                    Assert.True(File.Exists(snapshotPath));
                }
                finally
                {
                    Directory.Delete(root, recursive: true);
                }
            });
        }

        [Fact]
        public void HostBitmapWorkflow_SessionAndPackageKeepGray16PixelsAndRois()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                string root = CreateTempRoot();
                try
                {
                    var policy = new LocalAppDataImageViewerSessionStoragePolicy(root, TimeSpan.FromMinutes(5));
                    var registry = RoiPluginRegistry.CreateBuiltIn();
                    string sessionPath = Path.Combine(root, "memory.ivsession");
                    string packagePath = Path.Combine(root, "memory.ivpkg");
                    var dialogs = new SaveTargets(sessionPath, packagePath);
                    using var runtime = ImageViewerTestServices.CreateRuntimeServices(sessionStoragePolicy: policy, fileDialogService: dialogs);
                    using var host = ImageViewerTestServices.CreateHost(registry, runtime, CreateRecoveryHostServices(policy));
                    using var editor = host.CreateViewer();
                    ushort[] samples = [4, 1000, 32768, 65500];
                    BitmapSource bitmap = CreateGray16(samples);
                    editor.SetImage(bitmap);
                    editor.ViewerState.ReplaceAllRois([new LineMeasureRoi { P1 = new(0, 0), P2 = new(1, 1), Label = "sample" }]);
                    WorkpieceCoordinateSystem coordinates = WorkpieceCoordinateSystem.Localize(new(5, 7), new(5, 107),
                        new(30, 40), new(130, 40), "fixture");
                    editor.WorkpieceCoordinateSystem = coordinates;
                    editor.SetRecipeReferenceDatums([new() { Name = "R1", X = 1, Y = 2 }, new() { Name = "R2", X = 10, Y = 20 }]);
                    editor.SetRecipeCurrentDatums([new() { Name = "C1", X = 3, Y = 4 }, new() { Name = "C2", X = 30, Y = 40 }]);
                    await editor.SaveSessionAsync();
                    await editor._controlComposition.SessionController.ExportProjectPackageAsync();

                    using var reader = host.CreateViewer();
                    Assert.True(await reader.OpenProjectFromFileAsync(sessionPath));
                    Assert.Equal("sample", Assert.Single(reader.ViewerState.AllRois).Label);
                    AssertGray16Samples(reader.ImageSource, samples);
                    Assert.Equal(coordinates, reader.WorkpieceCoordinateSystem);
                    Assert.Equal(new PointD(30, 40), reader.WorkpieceCoordinateSystem!.ToWorkpiece(new(5, 7)));
                    Assert.Equal("R1", reader.GetRecipeReferenceDatums()[0].Name);
                    Assert.Equal(40, reader.GetRecipeCurrentDatums()[1].Y);
                    Assert.True(await reader.OpenProjectFromFileAsync(packagePath));
                    Assert.Equal("sample", Assert.Single(reader.ViewerState.AllRois).Label);
                    AssertGray16Samples(reader.ImageSource, samples);
                    Assert.Equal(coordinates, reader.WorkpieceCoordinateSystem);
                    Assert.Equal("R2", reader.GetRecipeReferenceDatums()[1].Name);
                    Assert.Equal("C1", reader.GetRecipeCurrentDatums()[0].Name);
                    Assert.Single(Directory.GetFiles(Path.Combine(root, "ImageViewer.assets"), "*.png"));
                }
                finally
                {
                    Directory.Delete(root, recursive: true);
                }
            });
        }

        [Fact]
        public void RecoveryWorkflow_ResavingInMemoryBitmapRetiresTemporaryImageWithoutLosingPixels()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                string root = CreateTempRoot();
                try
                {
                    var policy = new LocalAppDataImageViewerSessionStoragePolicy(root, TimeSpan.FromMinutes(5));
                    var registry = RoiPluginRegistry.CreateBuiltIn();
                    Directory.CreateDirectory(policy.AutoSaveDirectory);
                    string recoveryPath = Path.Combine(policy.AutoSaveDirectory, "autosave-memory.ivsession");
                    ushort[] samples = [5, 1200, 34000, 65000];
                    BitmapSource image = CreateGray16(samples);
                    var snapshot = new ImageViewerPersistenceSnapshot(null, RoiPersistenceService.CreateDocument([], 1, "px", registry), 1, 0, 0, null)
                    {
                        InMemoryImage = image,
                        CoordinateSystem = new WorkpieceCoordinateSystem { Origin = new(10, 20), AxisX = new(0, 1) },
                        ReferenceDatumPoints = [new RecipeDatumPoint { X = 1, Y = 2 }],
                        CurrentDatumPoints = [new RecipeDatumPoint { X = 3, Y = 4 }]
                    };
                    IImageViewerSessionService sessionService = new ImageViewerSessionService();
                    await sessionService.SaveRecoveryToFileAsync(recoveryPath, snapshot, registry);
                    string recoveredImage = (await sessionService.LoadFromFileAsync(recoveryPath, registry)).ImagePath!;
                    await sessionService.SaveRecoveryToFileAsync(recoveryPath, snapshot, registry);
                    Assert.Equal(recoveredImage, (await sessionService.LoadFromFileAsync(recoveryPath, registry)).ImagePath);
                    new ImageViewerRecoveryIndex(policy.AutoSaveDirectory).Register(recoveryPath, DateTimeOffset.UtcNow);

                    string savedPath = Path.Combine(root, "restored.ivsession");
                    string packagePath = Path.Combine(root, "restored.ivpkg");
                    using var runtime = ImageViewerTestServices.CreateRuntimeServices(sessionStoragePolicy: policy, fileDialogService: new SaveTargets(savedPath, packagePath));
                    using var host = ImageViewerTestServices.CreateHost(registry, runtime, CreateRecoveryHostServices(policy));
                    using var editor = host.CreateViewer();
                    await editor._controlComposition.SessionController.RecoverLatestAutoSaveAsync();
                    AssertGray16Samples(editor.ImageSource, samples);
                    Assert.Equal(snapshot.CoordinateSystem, editor.WorkpieceCoordinateSystem);
                    Assert.Equal(1, Assert.Single(editor.GetRecipeReferenceDatums()).X);
                    Assert.Equal(4, Assert.Single(editor.GetRecipeCurrentDatums()).Y);
                    await editor.SaveSessionAsync();
                    Assert.False(File.Exists(recoveryPath));
                    Assert.False(File.Exists(recoveredImage));
                    await editor._controlComposition.SessionController.ExportProjectPackageAsync();
                    using var reader = host.CreateViewer();
                    Assert.True(await reader.OpenProjectFromFileAsync(packagePath));
                    AssertGray16Samples(reader.ImageSource, samples);
                    Assert.Equal(snapshot.CoordinateSystem, reader.WorkpieceCoordinateSystem);
                    Assert.Equal(1, Assert.Single(reader.GetRecipeReferenceDatums()).X);
                }
                finally
                {
                    Directory.Delete(root, recursive: true);
                }
            });
        }

        [Fact]
        public void RecoveryImageReplacement_KeepsTheOldAssetIfTheSessionCommitFails()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                string root = CreateTempRoot();
                try
                {
                    string recoveryPath = Path.Combine(root, "autosave-atomic.ivsession");
                    var registry = RoiPluginRegistry.CreateBuiltIn();
                    var original = new ImageViewerPersistenceSnapshot(null, RoiPersistenceService.CreateDocument([], 1, "px", registry), 1, 0, 0, null)
                    {
                        InMemoryImage = CreateGray16([1, 2, 3, 4])
                    };
                    var updated = original with { InMemoryImage = CreateGray16([10, 20, 30, 40]) };
                    IImageViewerSessionService service = new ImageViewerSessionService();
                    await service.SaveRecoveryToFileAsync(recoveryPath, original, registry);
                    string oldImage = (await service.LoadFromFileAsync(recoveryPath, registry)).ImagePath!;
                    using (FileStream lockSession = File.Open(recoveryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        await Assert.ThrowsAnyAsync<IOException>(() => service.SaveRecoveryToFileAsync(recoveryPath, updated, registry));
                    }
                    Assert.Equal(oldImage, (await service.LoadFromFileAsync(recoveryPath, registry)).ImagePath);
                    Assert.Equal(oldImage, Assert.Single(Directory.GetFiles(Path.GetDirectoryName(oldImage)!)));

                    await service.SaveRecoveryToFileAsync(recoveryPath, updated, registry);
                    string newImage = (await service.LoadFromFileAsync(recoveryPath, registry)).ImagePath!;
                    Assert.NotEqual(oldImage, newImage);
                    Assert.False(File.Exists(oldImage));
                    AssertGray16Samples(await ImageViewerImageDecoder.LoadFromFileAsync(newImage), [10, 20, 30, 40]);
                }
                finally
                {
                    Directory.Delete(root, recursive: true);
                }
            });
        }

        [Fact]
        public void SnapshotImageCapture_DetachesPixelsFromAMutableBitmap()
        {
            WpfTestRunner.Run(() =>
            {
                var mutable = new WriteableBitmap(2, 2, 96, 96, PixelFormats.Gray16, null);
                var rect = new Int32Rect(0, 0, 2, 2);
                ushort[] before = [1, 1024, 32768, 65432];
                mutable.WritePixels(rect, before, 4, 0);
                BitmapSource detached = Assert.IsAssignableFrom<BitmapSource>(ImageViewerSnapshotImage.Capture(null, () => mutable).Bitmap);
                mutable.WritePixels(rect, new ushort[] { 0, 0, 0, 0 }, 4, 0);
                Assert.True(detached.IsFrozen);
                AssertGray16Samples(detached, before);
            });
        }

        [Fact]
        public void AdaptiveCloseSave_SavesOnlyDirtyViewsAndProtectsBothSessionFiles()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                string root = CreateTempRoot();
                try
                {
                    string primaryPath = Path.Combine(root, "primary.ivsession");
                    string axialPath = Path.Combine(root, "axial.ivsession");
                    string axialLabel = UiText.Get("SessionAxialView");
                    string primaryLabel = UiText.Get("SessionPrimaryView");
                    var labels = new List<string>();
                    string? axialTarget = null;
                    var dialogs = new SaveTargets("", "")
                    {
                        SelectNamedSession = label =>
                        {
                            labels.Add(label);
                            return label == axialLabel ? axialTarget : primaryPath;
                        }
                    };
                    var policy = new LocalAppDataImageViewerSessionStoragePolicy(root, TimeSpan.FromMinutes(5));
                    var registry = RoiPluginRegistry.CreateBuiltIn();
                    using var runtime = ImageViewerTestServices.CreateRuntimeServices(sessionStoragePolicy: policy, fileDialogService: dialogs);
                    using var host = ImageViewerTestServices.CreateHost(registry, runtime, CreateRecoveryHostServices(policy));
                    var volume = new VolumeData([CreateBitmap(3, 3, 10), CreateBitmap(3, 3, 20)]);
                    await using var viewer = new AdaptiveImageViewer(host.CreateViewer) { Volume = volume, ImageSource = volume.GetAxialSlice(0) };
                    viewer.MarkDocumentClean();
                    var axial = viewer.VolumeViewer.SliceViewer;
                    Assert.True(axial.AddRoi(new CircleRoi { Label = "axial", Radius = 1 }));
                    Assert.False(viewer.ImageViewer.IsDirty);
                    Assert.True(viewer.IsDirty);

                    Assert.False(await viewer.SaveUnsavedSessionsAsync());
                    Assert.Equal(axialLabel, Assert.Single(labels));
                    Assert.True(axial.IsDirty);

                    Assert.True(viewer.ImageViewer.AddRoi(new CircleRoi { Label = "primary", Radius = 1 }));
                    labels.Clear();
                    axialTarget = primaryPath;
                    Assert.False(await viewer.SaveUnsavedSessionsAsync());
                    Assert.Collection(labels, label => Assert.Equal(primaryLabel, label), label => Assert.Equal(axialLabel, label));
                    Assert.False(viewer.ImageViewer.IsDirty);
                    Assert.True(axial.IsDirty);
                    var service = new ImageViewerSessionService();
                    Assert.Equal("primary", Assert.Single((await service.LoadFromFileAsync(primaryPath, registry)).Rois).Label);

                    labels.Clear();
                    axialTarget = axialPath;
                    Assert.True(await viewer.SaveUnsavedSessionsAsync());
                    Assert.Equal(axialLabel, Assert.Single(labels));
                    Assert.False(viewer.IsDirty);
                    Assert.Equal("axial", Assert.Single((await service.LoadFromFileAsync(axialPath, registry)).Rois).Label);
                }
                finally
                {
                    Directory.Delete(root, recursive: true);
                }
            });
        }

        [Fact]
        public void MprWorkflow_NextSliceCommandUpdatesImageAnd3DPlane()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                using var viewer = new AdaptiveImageViewer
                {
                    Volume = new VolumeData([
                        CreateRowPattern(10),
                        CreateRowPattern(40),
                        CreateRowPattern(80)])
                };
                Assert.True(await viewer.SetDisplayModeAsync(AdaptiveDisplayMode.Coronal));
                int before = viewer.Volume3DViewer.CurrentCoronalSliceIndex;

                viewer.mprNextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (viewer.mprRebuildOverlay.Visibility == Visibility.Visible)
                {
                    await Task.Delay(10, timeout.Token);
                }

                Assert.Equal(before + 1, viewer.Volume3DViewer.CurrentCoronalSliceIndex);
                Assert.IsType<ImageViewer.Controls.ImageViewer>(viewer.ActiveView);
                var slice = Assert.IsAssignableFrom<BitmapSource>(viewer.ImageViewer.ImageSource);
                byte[] pixels = new byte[12];
                slice.CopyPixels(pixels, 4, 0);
                Assert.Equal(new byte[] { 12, 12, 12, 12, 42, 42, 42, 42, 82, 82, 82, 82 }, pixels);
            });

            static BitmapSource CreateRowPattern(byte value) =>
                BitmapSource.Create(4, 3, 96, 96, PixelFormats.Gray8, null,
                    Enumerable.Range(0, 12).Select(index => (byte)(value + index / 4)).ToArray(), 4);
        }

        private static string CreateTempRoot()
        {
            string path = Path.Combine(Path.GetTempPath(), $"image-viewer-e2e-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return path;
        }

        private static ImageViewerHostServices CreateRecoveryHostServices(IImageViewerSessionStoragePolicy policy)
        {
            ImageViewerHostServices defaults = ImageViewerHostDefaults.CreateHostServices();
            return new ImageViewerHostServices(defaults.DispatcherTimerFactory, defaults.RefreshSchedulerFactory,
                defaults.LatestTaskSchedulerFactory, defaults.PeriodicTaskSchedulerFactory, defaults.AnalysisDiagnostics, policy);
        }

        private static BitmapSource CreateGray16(ushort[] samples)
        {
            BitmapSource bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Gray16, null, samples, 4);
            bitmap.Freeze();
            return bitmap;
        }

        private static ImageViewerPersistenceSnapshot CreateRoiSession(string? imagePath, RoiPluginRegistry registry)
            => new(imagePath,
                RoiPersistenceService.CreateDocument([new CircleRoi { Label = "persisted", Center = new(1, 1), Radius = 1 }], 0.5, "mm", registry),
                1, 0, 0, null);

        private static void AssertGray16Samples(ImageSource? image, ushort[] expected)
        {
            var source = Assert.IsAssignableFrom<BitmapSource>(image);
            Assert.Equal(PixelFormats.Gray16, source.Format);
            var samples = new ushort[expected.Length];
            source.CopyPixels(samples, 4, 0);
            Assert.Equal(expected, samples);
        }

        private sealed class SaveTargets(string session, string package) : IImageViewerFileDialogService
        {
            public Func<string, string, string?>? LocateSessionImage { get; init; }
            public Func<string, string?>? SelectNamedSession { get; init; }
            public Func<string?>? SelectAnalysisCsv { get; init; }
            public Func<Window?, string?>? SelectImage { get; init; }

            public string? ShowLocateSessionImageDialog(Window? owner, string missingImagePath, string sessionPath)
                => LocateSessionImage?.Invoke(missingImagePath, sessionPath);

            public string ShowSaveSessionDialog(Window? owner) => session;
            public string? ShowSaveSessionDialog(Window? owner, string sessionLabel) =>
                SelectNamedSession != null ? SelectNamedSession(sessionLabel) : session;
            public string ShowSaveProjectPackageDialog(Window? owner) => package;
            public string? ShowOpenImageDialog(Window? owner) => SelectImage?.Invoke(owner);
            public string[] ShowOpenImageFilesDialog(Window? owner) => [];
            public string? ShowSaveRoiDialog(Window? owner) => null;
            public string? ShowOpenRoiDialog(Window? owner) => null;
            public string? ShowOpenSessionDialog(Window? owner) => null;
            public string? ShowSaveSnapshotDialog(Window? owner) => null;
            public string? ShowSaveAnalysisCsvDialog(Window? owner) => SelectAnalysisCsv?.Invoke();
            public string? ShowSaveXpsDialog(Window? owner) => null;
            public string? ShowSaveRecipeDialog(Window? owner) => null;
            public string? ShowOpenRecipeDialog(Window? owner) => null;
        }

        private static BitmapSource CreateBitmap(int width, int height, byte value)
        {
            BitmapSource bitmap = BitmapSource.Create(
                width,
                height,
                96,
                96,
                PixelFormats.Gray8,
                null,
                Enumerable.Repeat(value, width * height).Select(item => (byte)item).ToArray(),
                width);
            bitmap.Freeze();
            return bitmap;
        }

        private static void SaveBitmap(string path, BitmapSource bitmap)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream stream = File.Create(path);
            encoder.Save(stream);
        }
    }
}
