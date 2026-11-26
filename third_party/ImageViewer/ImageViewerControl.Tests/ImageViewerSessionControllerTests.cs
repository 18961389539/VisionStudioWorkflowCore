using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ImageViewer.Abstractions;
using ImageViewer.Controls;
using ImageViewer.Core.Geometry;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests
{
    [Collection(WpfTestCollection.Name)]
    [Trait("Category", "Integration")]
    [Trait("Category", "Wpf")]
    public class ImageViewerSessionControllerTests
    {
        [Fact]
        public void PersistenceSnapshot_KeepsCapturedCalibrationWhenTheLiveCalibrationChanges()
        {
            var calibration = new CameraCalibration
            {
                PrincipalX = 100, PrincipalY = 80, NormalizationRadius = 120,
                K1 = 0.01, K2 = -0.002, K3 = 0.0003,
                TangentialP1 = 0.0004, TangentialP2 = -0.0005,
                ReprojectionErrorRms = 0.2, MeasurementUncertaintyPixels = 0.03,
                CalibrationId = "captured", CalibratedAtUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)
            };
            var host = new RecordingSessionHost(new RecordingSessionService(), new RecordingRecentProjectService(), new RecordingProjectPackageService())
            {
                Calibration = calibration
            };
            var dependencies = CreateDependencies(host);
            ImageViewerPersistenceSnapshot snapshot = dependencies.Persistence.CaptureSnapshot();
            var service = new ImageViewerSessionService();
            string capturedJson = service.SerializeSession("captured", snapshot, host.PluginRegistry);

            calibration.PrincipalX = 999;
            calibration.K1 = 0.5;
            calibration.CalibrationId = "edited";
            calibration.CalibratedAtUtc = null;
            string savedJson = service.SerializeSession("saved", snapshot, host.PluginRegistry);
            CameraCalibration saved = service.LoadFromJson(savedJson, host.PluginRegistry).Calibration!;
            CameraCalibration expected = service.LoadFromJson(capturedJson, host.PluginRegistry).Calibration!;

            Assert.NotSame(calibration, snapshot.Calibration);
            Assert.Equal(expected.CorrectPixelLength(new PointD(10, 20), new PointD(70, 40)),
                saved.CorrectPixelLength(new PointD(10, 20), new PointD(70, 40)));
            foreach (var property in typeof(CameraCalibration).GetProperties().Where(property => property.CanWrite))
            {
                Assert.Equal(property.GetValue(expected), property.GetValue(saved));
            }
        }

        [Fact]
        public void PersistenceSnapshot_DetachesReferenceAndCurrentDatumPoints()
        {
            WpfTestRunner.Run(() =>
            {
                var reference = new RecipeDatumPoint { Name = "reference", X = 12, Y = 34 };
                var current = new RecipeDatumPoint { Name = "current", X = 56, Y = 78 };
                var coordinates = new WorkpieceCoordinateSystem { Origin = new PointD(3, 4), AxisX = new VectorD(0, 1) };
                var host = new RecordingSessionHost(new RecordingSessionService(), new RecordingRecentProjectService(), new RecordingProjectPackageService())
                {
                    CoordinateSystem = coordinates,
                    RecipeDatumPoints = [reference],
                    CurrentRecipeDatumPoints = [current]
                };
                var workflows = CreateDependencies(host);
                ImageViewerPersistenceSnapshot snapshot = workflows.Persistence.CaptureSnapshot();

                reference.X = 999;
                current.Y = 999;

                Assert.Equal(coordinates, snapshot.CoordinateSystem);
                Assert.Equal(12, Assert.Single(snapshot.ReferenceDatumPoints).X);
                Assert.Equal(78, Assert.Single(snapshot.CurrentDatumPoints).Y);
            });
        }

        [Fact]
        public void SaveSessionAsync_SavesStateAndTracksRecentProject()
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = CreateTempRoot();
                try
                {
                    var sessionService = new RecordingSessionService();
                    var recentProjectService = new RecordingRecentProjectService();
                    var packageService = new RecordingProjectPackageService();
                    var schedulerFactory = new RecordingPeriodicTaskSchedulerFactory();
                    var host = new RecordingSessionHost(sessionService, recentProjectService, packageService)
                    {
                        SaveSessionDialogPath = Path.Combine(rootPath, "sample.ivsession"),
                        CurrentImagePath = Path.Combine(rootPath, "source.png"),
                        AllRois = [new LineMeasureRoi()],
                        PixelSize = 0.25,
                        PhysicalUnit = "mm",
                        CurrentViewportState = new ImageViewerViewportState(1.5, 12, 18)
                    };

                    using var controller = CreateController(host, schedulerFactory, rootPath);

                    controller.SaveSessionAsync().GetAwaiter().GetResult();

                    Assert.Equal(host.SaveSessionDialogPath, sessionService.LastSaveFilePath);
                    Assert.Same(host.PluginRegistry, sessionService.LastSavePluginRegistry);

                    RecentImageViewerProject remembered = Assert.Single(recentProjectService.LastSavedItems!);
                    Assert.Equal("session", remembered.ProjectKind);
                    Assert.Equal(Path.GetFullPath(host.SaveSessionDialogPath!), remembered.Path);

                    ImageViewerDynamicMenuItem recentMenuItem = Assert.Single(controller.GetRecentProjectMenuItems());
                    Assert.Equal(remembered.Path, Assert.IsType<ImageViewerRecentProjectMenuTag>(recentMenuItem.Tag).ProjectPath);
                    Assert.Equal(1, host.UpdateContextMenuStateCount);
                    Assert.True(schedulerFactory.Scheduler.StartCalled);
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Fact]
        public void OpenProjectAsync_WhenFileIsMissing_RemovesRecentProjectAndWarns()
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = CreateTempRoot();
                try
                {
                    string missingFilePath = Path.Combine(rootPath, "missing.ivsession");
                    var sessionService = new RecordingSessionService();
                    var recentProjectService = new RecordingRecentProjectService();
                    recentProjectService.Seed(new RecentImageViewerProject("missing", missingFilePath, "session", DateTimeOffset.UtcNow));
                    var host = new RecordingSessionHost(sessionService, recentProjectService, new RecordingProjectPackageService());

                    using var controller = CreateController(
                        host,
                        new RecordingPeriodicTaskSchedulerFactory(),
                        rootPath);

                    controller.OpenProjectAsync(missingFilePath).GetAwaiter().GetResult();

                    Assert.Empty(recentProjectService.LastSavedItems!);
                    Assert.Single(host.Warnings);
                    Assert.Equal(1, host.UpdateContextMenuStateCount);
                    Assert.Null(sessionService.LastLoadFilePath);
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Fact]
        public void AutoSave_WhenThereIsNoContent_DoesNotWriteSession()
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = CreateTempRoot();
                try
                {
                    var sessionService = new RecordingSessionService();
                    var host = new RecordingSessionHost(sessionService, new RecordingRecentProjectService(), new RecordingProjectPackageService())
                    {
                        HasContent = false
                    };
                    var schedulerFactory = new RecordingPeriodicTaskSchedulerFactory();

                    using var controller = CreateController(host, schedulerFactory, rootPath);
                    schedulerFactory.Scheduler.InvokeCallbackAsync().GetAwaiter().GetResult();

                    Assert.Null(sessionService.LastSaveFilePath);
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Fact]
        public void AutoSave_WhenDisabled_DoesNotWriteSession()
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = CreateTempRoot();
                try
                {
                    var sessionService = new RecordingSessionService();
                    var host = new RecordingSessionHost(sessionService, new RecordingRecentProjectService(), new RecordingProjectPackageService());
                    var schedulerFactory = new RecordingPeriodicTaskSchedulerFactory();

                    using var controller = CreateController(host, schedulerFactory, rootPath);
                    controller.ToggleAutoSave();
                    schedulerFactory.Scheduler.InvokeCallbackAsync().GetAwaiter().GetResult();

                    Assert.Null(sessionService.LastSaveFilePath);
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Fact]
        public void AutoSave_WhenSaveFails_ShowsNonBlockingHint()
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = CreateTempRoot();
                try
                {
                    var sessionService = new RecordingSessionService { ThrowOnSave = true };
                    var host = new RecordingSessionHost(sessionService, new RecordingRecentProjectService(), new RecordingProjectPackageService())
                    {
                        HasContent = true
                    };
                    var schedulerFactory = new RecordingPeriodicTaskSchedulerFactory();

                    using var controller = CreateController(host, schedulerFactory, rootPath);
                    schedulerFactory.Scheduler.InvokeCallbackAsync().GetAwaiter().GetResult();

                    Assert.Contains(UiText.Get("StatusAutoSaveFailed"), host.StatusHints);
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Fact]
        public void AutoSave_WhenSaveSucceedsAfterFailure_ResetsFailureHint()
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = CreateTempRoot();
                try
                {
                    var sessionService = new RecordingSessionService();
                    var host = new RecordingSessionHost(sessionService, new RecordingRecentProjectService(), new RecordingProjectPackageService())
                    {
                        HasContent = true
                    };
                    var schedulerFactory = new RecordingPeriodicTaskSchedulerFactory();

                    using var controller = CreateController(host, schedulerFactory, rootPath);

                    sessionService.ThrowOnSave = true;
                    schedulerFactory.Scheduler.InvokeCallbackAsync().GetAwaiter().GetResult();
                    Assert.Single(host.StatusHints.Where(hint => hint == UiText.Get("StatusAutoSaveFailed")));

                    sessionService.ThrowOnSave = false;
                    schedulerFactory.Scheduler.InvokeCallbackAsync().GetAwaiter().GetResult();
                    schedulerFactory.Scheduler.InvokeCallbackAsync().GetAwaiter().GetResult();

                    Assert.Single(host.StatusHints.Where(hint => hint == UiText.Get("StatusAutoSaveFailed")));
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Fact]
        public void AutoSave_WhenReEnabledAfterFailure_AllowsNextFailureHint()
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = CreateTempRoot();
                try
                {
                    var sessionService = new RecordingSessionService { ThrowOnSave = true };
                    var host = new RecordingSessionHost(sessionService, new RecordingRecentProjectService(), new RecordingProjectPackageService())
                    {
                        HasContent = true
                    };
                    var schedulerFactory = new RecordingPeriodicTaskSchedulerFactory();

                    using var controller = CreateController(host, schedulerFactory, rootPath);

                    // 第一次失败提示一次
                    schedulerFactory.Scheduler.InvokeCallbackAsync().GetAwaiter().GetResult();
                    Assert.Single(host.StatusHints.Where(hint => hint == UiText.Get("StatusAutoSaveFailed")));

                    // 关闭后再重新开启：失败标记应被重置，下一次失败可再次提示
                    controller.ToggleAutoSave();
                    controller.ToggleAutoSave();
                    schedulerFactory.Scheduler.InvokeCallbackAsync().GetAwaiter().GetResult();

                    Assert.Equal(
                        2,
                        host.StatusHints.Count(hint => hint == UiText.Get("StatusAutoSaveFailed")));
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Fact]
        public void AutoSave_WithoutProjectPath_PreservesUnresolvedRoisInRecoverySnapshot()
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = CreateTempRoot();
                try
                {
                    // 从未保存过项目：_currentProjectPath 为 null，验证自动保存仍能真正写入而不崩溃
                    var sessionService = new RecordingSessionService();
                    var host = new RecordingSessionHost(sessionService, new RecordingRecentProjectService(), new RecordingProjectPackageService())
                    {
                        HasContent = true
                    };
                    var schedulerFactory = new RecordingPeriodicTaskSchedulerFactory();

                    using var controller = CreateController(host, schedulerFactory, rootPath);
                    var unresolved = new RoiPersistenceData { Type = "missing-plugin", Label = "inspection" };
                    controller.AppendUnresolvedRois([unresolved]);

                    schedulerFactory.Scheduler.InvokeCallbackAsync().GetAwaiter().GetResult();

                    Assert.NotNull(sessionService.LastSaveFilePath);
                    RoiPersistenceData saved = Assert.Single(sessionService.LastSavedSnapshot!.RoiDocument.Items);
                    Assert.Equal((unresolved.Type, unresolved.Label), (saved.Type, saved.Label));
                    Assert.Empty(host.LoggedErrors);
                    Assert.Empty(host.StatusHints);
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Theory]
        [InlineData(MessageBoxResult.Yes, true, false, true)]
        [InlineData(MessageBoxResult.Yes, false, false, false)]
        [InlineData(MessageBoxResult.Yes, true, true, false)]
        [InlineData(MessageBoxResult.No, false, false, true)]
        [InlineData(MessageBoxResult.Cancel, false, false, false)]
        public void OpenProjectAsync_DirtyDocumentRespectsSaveDecision(
            MessageBoxResult decision, bool chooseSavePath, bool failSave, bool opened)
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = CreateTempRoot();
                try
                {
                    string nextProject = Path.Combine(rootPath, "next.ivsession");
                    string savedProject = Path.Combine(rootPath, "saved.ivsession");
                    File.WriteAllText(nextProject, "session");
                    var sessionService = new RecordingSessionService { ThrowOnSave = failSave };
                    var originalRoi = new LineMeasureRoi();
                    var host = new RecordingSessionHost(sessionService, new RecordingRecentProjectService(), new RecordingProjectPackageService())
                    {
                        AllRois = [originalRoi],
                        UnsavedChangesDecision = decision,
                        SaveSessionDialogPath = chooseSavePath ? savedProject : null
                    };
                    using var controller = CreateController(host, new RecordingPeriodicTaskSchedulerFactory(), rootPath);
                    controller.MarkDirty();

                    controller.OpenProjectAsync(nextProject).GetAwaiter().GetResult();

                    Assert.Equal(1, host.UnsavedChangesPromptCount);
                    Assert.Equal(opened, host.LastReplacedRois != null);
                    Assert.Equal(!opened, controller.IsDirty);
                    Assert.Equal(opened ? 1 : 0, host.ClearUndoHistoryCount);
                    Assert.Equal(decision == MessageBoxResult.Yes && chooseSavePath && !failSave, File.Exists(savedProject));
                    if (!opened)
                    {
                        Assert.Same(originalRoi, Assert.Single(host.AllRois));
                    }
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Fact]
        public void OpenProjectAsync_SessionFile_LoadsAndAppliesSessionState()
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = CreateTempRoot();
                try
                {
                    string sessionFilePath = Path.Combine(rootPath, "project.ivsession");
                    string imageFilePath = Path.Combine(rootPath, "image.png");
                    File.WriteAllText(sessionFilePath, "session");
                    File.WriteAllText(imageFilePath, "image");

                    var sessionService = new RecordingSessionService
                    {
                        LoadResult = new ImageViewerSessionData(
                            "session",
                            DateTimeOffset.UtcNow,
                            imageFilePath,
                            [new LineMeasureRoi()],
                            1.75,
                            "um",
                            2.5,
                            30,
                            40,
                            null)
                    };
                    var recentProjectService = new RecordingRecentProjectService();
                    var host = new RecordingSessionHost(sessionService, recentProjectService, new RecordingProjectPackageService());

                    using var controller = CreateController(host, new RecordingPeriodicTaskSchedulerFactory(), rootPath);

                    controller.OpenProjectAsync(sessionFilePath).GetAwaiter().GetResult();

                    Assert.Equal(sessionFilePath, sessionService.LastLoadFilePath);
                    Assert.Equal(imageFilePath, host.LastLoadedImagePath);
                    Assert.False(host.LastLoadImageFitToView);
                    Assert.Single(host.LastReplacedRois!);
                    Assert.Equal(1.75, host.PixelSize);
                    Assert.Equal("um", host.PhysicalUnit);
                    Assert.Equal(new ImageViewerViewportState(2.5, 30, 40), host.LastAppliedViewportState);
                    Assert.Equal(1, host.DrawRoisCallCount);
                    Assert.Equal(1, host.ClearUndoHistoryCount);

                    RecentImageViewerProject remembered = Assert.Single(recentProjectService.LastSavedItems!);
                    Assert.Equal("session", remembered.ProjectKind);
                    Assert.Equal(2, host.UpdateContextMenuStateCount);
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Fact]
        public void ExportProjectPackageAsync_UsesPackageServiceAndTracksPackageProject()
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = CreateTempRoot();
                try
                {
                    var sessionService = new RecordingSessionService();
                    var recentProjectService = new RecordingRecentProjectService();
                    var packageService = new RecordingProjectPackageService();
                    var host = new RecordingSessionHost(sessionService, recentProjectService, packageService)
                    {
                        SaveProjectPackageDialogPath = Path.Combine(rootPath, "sample.ivpkg"),
                        CurrentImagePath = Path.Combine(rootPath, "source.png"),
                        AllRois = [new LineMeasureRoi()],
                        PixelSize = 0.5,
                        PhysicalUnit = "mm",
                        CurrentViewportState = new ImageViewerViewportState(1.2, 4, 6)
                    };

                    using var controller = CreateController(host, new RecordingPeriodicTaskSchedulerFactory(), rootPath);

                    controller.ExportProjectPackageAsync().GetAwaiter().GetResult();

                    Assert.Equal(host.SaveProjectPackageDialogPath, packageService.LastExportFilePath);
                    Assert.Same(host.PluginRegistry, packageService.LastExportPluginRegistry);

                    RecentImageViewerProject remembered = Assert.Single(recentProjectService.LastSavedItems!);
                    Assert.Equal("package", remembered.ProjectKind);
                    Assert.Equal(Path.GetFullPath(host.SaveProjectPackageDialogPath!), remembered.Path);
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Fact]
        public void ToggleAutoSave_FlipsEnabledState()
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = CreateTempRoot();
                try
                {
                    var host = new RecordingSessionHost(new RecordingSessionService(), new RecordingRecentProjectService(), new RecordingProjectPackageService());
                    using var controller = CreateController(host, new RecordingPeriodicTaskSchedulerFactory(), rootPath);

                    bool initialState = controller.IsAutoSaveEnabled;

                    controller.ToggleAutoSave();

                    Assert.NotEqual(initialState, controller.IsAutoSaveEnabled);
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Fact]
        public void RecoverLatestAutoSave_DiscoversUnsavedDocumentFromPreviousRun()
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = CreateTempRoot();
                try
                {
                    // 第一次运行：一个未保存文档自动保存后**异常退出**（崩溃/强杀，走不到正常关闭的清理路径）。
                    // 遗留的标志是"索引里有一条，但所有者心跳已经过期"——用过期时间戳直接写索引来模拟。
                    var firstService = new RecordingSessionService();
                    var firstHost = new RecordingSessionHost(firstService, new RecordingRecentProjectService(), new RecordingProjectPackageService()) { HasContent = true };
                    var firstScheduler = new RecordingPeriodicTaskSchedulerFactory();
                    using (var firstController = CreateController(firstHost, firstScheduler, rootPath, "run-one-document"))
                    {
                        firstScheduler.Scheduler.InvokeCallbackAsync().GetAwaiter().GetResult();
                        // Simulate a crashed owner without performing that owner's graceful cleanup.
                        WriteStaleHeartbeatEntry(rootPath, firstService.LastSaveFilePath!, "crashed-run-one");
                    }

                    string autoSaveFileName = Path.GetFileName(firstService.LastSaveFilePath!);
                    Assert.Equal("autosave-run-one-document.ivsession", autoSaveFileName);

                    // 第二次运行：全新的 DocumentId（模拟重启），必须仍能发现上次崩溃留下的未保存文档。
                    string sourceImage = Path.Combine(rootPath, "source.png");
                    File.WriteAllText(sourceImage, "image");
                    var secondService = new RecordingSessionService
                    {
                        LoadResult = new ImageViewerSessionData(
                            sourceImage,
                            DateTimeOffset.UtcNow,
                            null,
                            [],
                            1.0,
                            "px",
                            1.0,
                            0,
                            0,
                            null)
                    };
                    var secondHost = new RecordingSessionHost(secondService, new RecordingRecentProjectService(), new RecordingProjectPackageService());
                    using var secondController = CreateController(secondHost, new RecordingPeriodicTaskSchedulerFactory(), rootPath, "run-two-document");

                    Assert.True(secondController.HasRecoverySnapshot);

                    secondController.RecoverLatestAutoSaveAsync().GetAwaiter().GetResult();

                    Assert.EndsWith(autoSaveFileName, secondService.LastLoadFilePath, StringComparison.OrdinalIgnoreCase);
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Fact]
        public void RecoverAutoSave_OffersNewestFirstAndCanRestoreAnOlderSnapshot()
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = CreateTempRoot();
                try
                {
                    string autoSaveDirectory = Path.Combine(rootPath, "Autosave");
                    Directory.CreateDirectory(autoSaveDirectory);
                    string older = Path.Combine(autoSaveDirectory, "autosave-older.ivsession");
                    string newer = Path.Combine(autoSaveDirectory, "autosave-newer.ivsession");
                    File.WriteAllText(older, "older");
                    File.WriteAllText(newer, "newer");
                    File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-2));
                    WriteStaleHeartbeatEntry(rootPath, older, "older-owner");
                    var index = new ImageViewerRecoveryIndex(autoSaveDirectory);
                    index.Register(newer, DateTimeOffset.UtcNow, projectName: "newest.ivpkg", imageName: "inspection.png");
                    var service = new RecordingSessionService();
                    var host = new RecordingSessionHost(service, new RecordingRecentProjectService(), new RecordingProjectPackageService());
                    using var controller = CreateController(host, new RecordingPeriodicTaskSchedulerFactory(), rootPath);

                    controller.RecoverAutoSaveAsync(choices =>
                    {
                        Assert.Equal([newer, older], choices.Select(choice => choice.Path));
                        Assert.Equal("newest.ivpkg", choices[0].ProjectName);
                        Assert.Equal("inspection.png", choices[0].ImageName);
                        return choices[1];
                    }).GetAwaiter().GetResult();

                    Assert.Equal(older, service.LastLoadFilePath);
                    Assert.True(controller.IsDirty);
                    Assert.True(controller.HasRecoverySnapshot);
                    controller.RecoverAutoSaveAsync(choices =>
                    {
                        Assert.Equal(newer, Assert.Single(choices).Path);
                        return null;
                    }).GetAwaiter().GetResult();
                    Assert.Equal(older, service.LastLoadFilePath);
                    Assert.True(File.Exists(newer));
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Fact]
        public void RecoverAutoSave_WhenSelectedSnapshotChangesWhilePickerIsOpen_DoesNotReplaceDocument()
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = CreateTempRoot();
                try
                {
                    string recovery = Path.Combine(rootPath, "Autosave", "autosave.ivsession");
                    Directory.CreateDirectory(Path.GetDirectoryName(recovery)!);
                    File.WriteAllText(recovery, "earlier");
                    var service = new RecordingSessionService();
                    var host = new RecordingSessionHost(service, new RecordingRecentProjectService(), new RecordingProjectPackageService());
                    using var controller = CreateController(host, new RecordingPeriodicTaskSchedulerFactory(), rootPath);

                    controller.RecoverAutoSaveAsync(choices =>
                    {
                        RecoverySnapshotOption original = Assert.Single(choices);
                        File.WriteAllText(recovery, "new contents");
                        File.SetLastWriteTimeUtc(recovery, original.LastWriteUtc.AddMinutes(1));
                        return original;
                    }).GetAwaiter().GetResult();

                    Assert.Null(service.LastLoadFilePath);
                    Assert.Null(host.LastReplacedRois);
                    Assert.True(controller.HasRecoverySnapshot);
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Fact]
        public void RecoverAutoSave_CanOfferAFreshCrashSnapshotAsSoonAsTheLeaseIsReleased()
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = CreateTempRoot();
                try
                {
                    string directory = Path.Combine(rootPath, "Autosave");
                    Directory.CreateDirectory(directory);
                    string recovery = Path.Combine(directory, "autosave-crash.ivsession");
                    File.WriteAllText(recovery, "work");
                    string owner = Guid.NewGuid().ToString("N");
                    var index = new ImageViewerRecoveryIndex(directory);
                    var service = new RecordingSessionService();
                    var host = new RecordingSessionHost(service, new RecordingRecentProjectService(), new RecordingProjectPackageService());
                    using var editor = CreateController(host, new RecordingPeriodicTaskSchedulerFactory(), rootPath);
                    using (FileStream lease = Assert.IsType<FileStream>(index.TryAcquireOwnerLease(owner)))
                    {
                        DateTimeOffset now = DateTimeOffset.UtcNow;
                        index.Register(recovery, now, owner, now, hasOwnerLease: true);
                        editor.RecoverLatestAutoSaveAsync().GetAwaiter().GetResult();
                        Assert.Null(service.LastLoadFilePath);
                    }

                    editor.RecoverLatestAutoSaveAsync().GetAwaiter().GetResult();
                    Assert.Equal(recovery, service.LastLoadFilePath);
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Fact]
        public void AutoSave_ClosingWhileAWriteIsPendingFinishesCleanup()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                string rootPath = CreateTempRoot();
                try
                {
                    var service = new RecordingSessionService();
                    var host = new RecordingSessionHost(service, new RecordingRecentProjectService(), new RecordingProjectPackageService());
                    var scheduler = new RecordingPeriodicTaskSchedulerFactory();
                    using var editor = CreateController(host, scheduler, rootPath);
                    await scheduler.Scheduler.InvokeCallbackAsync();
                    string snapshot = service.LastSaveFilePath!;
                    Assert.True(File.Exists(snapshot));

                    var blockedWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    service.AllowAutoSave = blockedWrite.Task;
                    Task writing = scheduler.Scheduler.InvokeCallbackAsync();
                    Assert.False(writing.IsCompleted);
                    editor.Dispose();
                    await Task.WhenAll(writing, editor.CloseCompletion).WaitAsync(TimeSpan.FromSeconds(5));

                    Assert.False(File.Exists(snapshot));
                    Assert.Empty(new ImageViewerRecoveryIndex(Path.GetDirectoryName(snapshot)!).Read());
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Fact]
        public void AutoSave_PausingPreservesLiveSnapshotAndClosingDeletesIt()
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = CreateTempRoot();
                try
                {
                    var service = new RecordingSessionService();
                    var host = new RecordingSessionHost(service, new RecordingRecentProjectService(), new RecordingProjectPackageService());
                    var scheduler = new RecordingPeriodicTaskSchedulerFactory();
                    string snapshot;
                    var index = new ImageViewerRecoveryIndex(Path.Combine(rootPath, "Autosave"));
                    using (var editor = CreateController(host, scheduler, rootPath, "editor"))
                    {
                        scheduler.Scheduler.InvokeCallbackAsync().GetAwaiter().GetResult();
                        snapshot = service.LastSaveFilePath!;
                        editor.StopAutoSave();
                        editor.DrainAutoSaveAsync().GetAwaiter().GetResult();
                        ImageViewerRecoveryIndexEntry entry = Assert.Single(index.Read());
                        index.Register(snapshot, entry.TimestampUtc, entry.OwnerInstanceId, DateTimeOffset.UtcNow.AddHours(-1));
                        using var observer = CreateController(
                            new RecordingSessionHost(new RecordingSessionService(), new RecordingRecentProjectService(), new RecordingProjectPackageService()),
                            new RecordingPeriodicTaskSchedulerFactory(), rootPath, "observer");
                        Assert.True(File.Exists(snapshot));
                        Assert.False(observer.HasRecoverySnapshot);
                    }
                    Assert.False(File.Exists(snapshot));
                    Assert.Empty(index.Read());
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Theory]
        [InlineData(MessageBoxResult.No, false)]
        [InlineData(MessageBoxResult.Cancel, true)]
        public void OpenProjectAsync_CleansThePreviousSnapshotOnlyAfterTheReplacementCommits(
            MessageBoxResult decision, bool keepsSnapshot)
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = CreateTempRoot();
                try
                {
                    string project = Path.Combine(rootPath, "next.ivsession");
                    File.WriteAllText(project, "session");
                    var service = new RecordingSessionService();
                    var host = new RecordingSessionHost(service, new RecordingRecentProjectService(), new RecordingProjectPackageService())
                    {
                        UnsavedChangesDecision = decision
                    };
                    var scheduler = new RecordingPeriodicTaskSchedulerFactory();
                    using var editor = CreateController(host, scheduler, rootPath, "editor");
                    editor.MarkDirty();
                    scheduler.Scheduler.InvokeCallbackAsync().GetAwaiter().GetResult();
                    string snapshot = service.LastSaveFilePath!;
                    Assert.True(File.Exists(snapshot));

                    Assert.Equal(!keepsSnapshot, editor.OpenProjectAsync(project).GetAwaiter().GetResult());
                    Assert.Equal(keepsSnapshot, File.Exists(snapshot));
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Fact]
        public void RecoverLatestAutoSave_DoesNotOfferSnapshotStillHeldByALiveWindow()
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = CreateTempRoot();
                try
                {
                    // A 窗口保持打开并自动保存（心跳新鲜）。
                    var serviceA = new RecordingSessionService();
                    var hostA = new RecordingSessionHost(serviceA, new RecordingRecentProjectService(), new RecordingProjectPackageService()) { HasContent = true };
                    var schedulerA = new RecordingPeriodicTaskSchedulerFactory();
                    using var controllerA = CreateController(hostA, schedulerA, rootPath, "document-a");

                    schedulerA.Scheduler.InvokeCallbackAsync().GetAwaiter().GetResult();
                    string autoSavePathA = serviceA.LastSaveFilePath!;
                    Assert.True(File.Exists(autoSavePathA));

                    // B 窗口随后创建：A 的文档仍在另一个窗口里开着，不能把它的快照提示给 B 恢复。
                    var hostB = new RecordingSessionHost(new RecordingSessionService(), new RecordingRecentProjectService(), new RecordingProjectPackageService());
                    using var controllerB = CreateController(hostB, new RecordingPeriodicTaskSchedulerFactory(), rootPath, "document-b");

                    Assert.False(controllerB.HasRecoverySnapshot);
                    Assert.True(File.Exists(autoSavePathA));
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Fact]
        public void RecoverLatestAutoSave_DoesNotOfferDocumentsAlreadySavedForReal()
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = CreateTempRoot();
                try
                {
                    var firstService = new RecordingSessionService();
                    var firstHost = new RecordingSessionHost(firstService, new RecordingRecentProjectService(), new RecordingProjectPackageService()) { HasContent = true };
                    var firstScheduler = new RecordingPeriodicTaskSchedulerFactory();
                    using var firstController = CreateController(firstHost, firstScheduler, rootPath, "run-one-document");

                    // 自动保存一次（进入索引），随后用户真正保存到磁盘并清理了自动保存。
                    firstScheduler.Scheduler.InvokeCallbackAsync().GetAwaiter().GetResult();
                    string autoSavePath = firstService.LastSaveFilePath!;
                    Assert.True(File.Exists(autoSavePath));

                    firstHost.SaveSessionDialogPath = Path.Combine(rootPath, "saved.ivsession");
                    firstController.SaveSessionAsync().GetAwaiter().GetResult();

                    Assert.False(File.Exists(autoSavePath), $"自动保存文件应被清理，但仍存在：{autoSavePath}");

                    // 重启：已经保存过的文档不应再被当成待恢复文档。
                    var secondHost = new RecordingSessionHost(new RecordingSessionService(), new RecordingRecentProjectService(), new RecordingProjectPackageService());
                    using var secondController = CreateController(secondHost, new RecordingPeriodicTaskSchedulerFactory(), rootPath, "run-two-document");

                    Assert.False(secondController.HasRecoverySnapshot);
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Fact]
        public void RecoverLatestAutoSave_IgnoresUnindexedAutoSaves()
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = CreateTempRoot();
                try
                {
                    // 目录里躺着一个自动保存，但恢复索引里没有登记它（例如是别的进程/手工放进来的）。
                    // 重启后不应把未登记的文件当作自己的恢复候选——索引才是"本机遗留文档"的权威来源。
                    string autoSaveDirectory = Path.Combine(rootPath, "Autosave");
                    Directory.CreateDirectory(autoSaveDirectory);
                    File.WriteAllText(Path.Combine(autoSaveDirectory, "autosave-unindexed.ivsession"), "unindexed");

                    var host = new RecordingSessionHost(new RecordingSessionService(), new RecordingRecentProjectService(), new RecordingProjectPackageService());
                    using var controller = CreateController(host, new RecordingPeriodicTaskSchedulerFactory(), rootPath, "run-two-document");

                    Assert.False(controller.HasRecoverySnapshot);
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Fact]
        public void SaveSessionAsync_OnlyCleansUpItsOwnAutoSave_NotOtherDocumentsRecoveryFiles()
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = CreateTempRoot();
                try
                {
                    // 两个窗口共用同一个 Autosave 目录，各自自动保存一次。
                    var firstService = new RecordingSessionService();
                    var secondService = new RecordingSessionService();
                    var firstHost = new RecordingSessionHost(firstService, new RecordingRecentProjectService(), new RecordingProjectPackageService()) { HasContent = true };
                    var secondHost = new RecordingSessionHost(secondService, new RecordingRecentProjectService(), new RecordingProjectPackageService()) { HasContent = true };
                    var firstScheduler = new RecordingPeriodicTaskSchedulerFactory();
                    var secondScheduler = new RecordingPeriodicTaskSchedulerFactory();

                    using var firstController = CreateController(firstHost, firstScheduler, rootPath, "document-one");
                    using var secondController = CreateController(secondHost, secondScheduler, rootPath, "document-two");

                    firstScheduler.Scheduler.InvokeCallbackAsync().GetAwaiter().GetResult();
                    secondScheduler.Scheduler.InvokeCallbackAsync().GetAwaiter().GetResult();

                    string firstAutoSave = firstService.LastSaveFilePath!;
                    string secondAutoSave = secondService.LastSaveFilePath!;
                    Assert.True(File.Exists(firstAutoSave));
                    Assert.True(File.Exists(secondAutoSave));

                    // 第一个窗口真正保存到磁盘：只应清理它自己的自动保存，绝不能碰第二个窗口的恢复文件。
                    firstHost.SaveSessionDialogPath = Path.Combine(rootPath, "saved.ivsession");
                    firstController.SaveSessionAsync().GetAwaiter().GetResult();

                    Assert.False(File.Exists(firstAutoSave), $"本文档的自动保存应被清理：{firstAutoSave}");
                    Assert.True(File.Exists(secondAutoSave), $"其它文档的恢复文件必须保留，但已被删除：{secondAutoSave}");
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SaveSessionAsync_CleansOnlyTheUnchangedRecoveryActuallyOpened(bool recoveryWasUpdated)
        {
            WpfTestRunner.Run(() =>
            {
                string root = CreateTempRoot();
                try
                {
                    string recovery = Path.Combine(root, "Autosave", "autosave-previous.ivsession");
                    Directory.CreateDirectory(Path.GetDirectoryName(recovery)!);
                    File.WriteAllText(recovery, "previous work");
                    WriteStaleHeartbeatEntry(root, recovery, "previous-window");
                    var service = new RecordingSessionService();
                    var host = new RecordingSessionHost(service, new RecordingRecentProjectService(), new RecordingProjectPackageService())
                    {
                        SaveSessionDialogPath = Path.Combine(root, "saved.ivsession")
                    };
                    using var controller = CreateController(host, new RecordingPeriodicTaskSchedulerFactory(), root);
                    Assert.True(controller.HasRecoverySnapshot);

                    controller.SaveSessionAsync().GetAwaiter().GetResult();
                    Assert.True(controller.HasRecoverySnapshot);
                    Assert.Equal("previous work", File.ReadAllText(recovery));

                    controller.RecoverLatestAutoSaveAsync().GetAwaiter().GetResult();
                    Assert.Equal(recovery, service.LastLoadFilePath);
                    if (recoveryWasUpdated)
                    {
                        File.WriteAllText(recovery, "newer recovery");
                        File.SetLastWriteTimeUtc(recovery, DateTime.UtcNow.AddMinutes(1));
                    }
                    controller.SaveSessionAsync().GetAwaiter().GetResult();
                    Assert.Equal(recoveryWasUpdated, File.Exists(recovery));
                    Assert.Equal(recoveryWasUpdated, controller.HasRecoverySnapshot);
                    if (recoveryWasUpdated)
                    {
                        Assert.Equal("newer recovery", File.ReadAllText(recovery));
                    }
                }
                finally
                {
                    Directory.Delete(root, recursive: true);
                }
            });
        }

        [Fact]
        public void SaveSessionAsync_AnInFlightAutoSaveCannotRecreateTheRecoveryFile()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                string root = CreateTempRoot();
                try
                {
                    var allowAutoSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var service = new RecordingSessionService { AllowAutoSave = allowAutoSave.Task };
                    var host = new RecordingSessionHost(service, new RecordingRecentProjectService(), new RecordingProjectPackageService())
                    {
                        SaveSessionDialogPath = Path.Combine(root, "saved.ivsession")
                    };
                    var scheduler = new RecordingPeriodicTaskSchedulerFactory();
                    using var controller = CreateController(host, scheduler, root, "same-document");
                    controller.MarkDirty();

                    Task autoSave = scheduler.Scheduler.InvokeCallbackAsync();
                    Task manualSave = controller.SaveSessionAsync();
                    try
                    {
                        Assert.True(File.Exists(host.SaveSessionDialogPath));
                        Assert.False(manualSave.IsCompleted);
                    }
                    finally
                    {
                        allowAutoSave.SetResult();
                        await Task.WhenAll(autoSave, manualSave);
                    }

                    string snapshot = Path.Combine(root, "Autosave", "autosave-same-document.ivsession");
                    Assert.False(File.Exists(snapshot));
                    Assert.False(controller.IsDirty);
                }
                finally
                {
                    Directory.Delete(root, recursive: true);
                }
            });
        }

        [Fact]
        public void ReplacingImageDocument_StartsAnIndependentAutoSave()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                string root = CreateTempRoot();
                try
                {
                    string project = Path.Combine(root, "old-project.ivsession");
                    File.WriteAllText(project, "session");
                    var service = new RecordingSessionService();
                    var host = new RecordingSessionHost(service, new RecordingRecentProjectService(), new RecordingProjectPackageService());
                    var scheduler = new RecordingPeriodicTaskSchedulerFactory();
                    using var controller = CreateController(host, scheduler, root, "new-document");
                    Assert.True(await controller.OpenProjectAsync(project));
                    await scheduler.Scheduler.InvokeCallbackAsync();
                    string oldSnapshot = service.LastSaveFilePath!;
                    Assert.Contains("autosave-old-project-", oldSnapshot, StringComparison.Ordinal);
                    Assert.True(File.Exists(oldSnapshot));

                    string newImage = Path.Combine(root, "new-image.png");
                    Assert.True(await controller.ReplaceImageDocumentAsync(
                        _ => Task.FromResult<Action?>(() => host.CurrentImagePath = newImage), CancellationToken.None));
                    Assert.False(File.Exists(oldSnapshot));
                    Assert.True(controller.IsDirty);
                    await scheduler.Scheduler.InvokeCallbackAsync();

                    Assert.Equal("autosave-new-document.ivsession", Path.GetFileName(service.LastSaveFilePath));
                    Assert.True(File.Exists(service.LastSaveFilePath));
                }
                finally
                {
                    Directory.Delete(root, recursive: true);
                }
            });
        }

        [Fact]
        public void AutoSave_TwoEditorsOfTheSameProjectKeepIndependentSnapshots()
        {
            WpfTestRunner.Run(() =>
            {
                string root = CreateTempRoot();
                try
                {
                    string project = Path.Combine(root, "shared.ivsession");
                    File.WriteAllText(project, "project");
                    var firstService = new RecordingSessionService();
                    var secondService = new RecordingSessionService();
                    var firstHost = new RecordingSessionHost(firstService, new RecordingRecentProjectService(), new RecordingProjectPackageService());
                    var secondHost = new RecordingSessionHost(secondService, new RecordingRecentProjectService(), new RecordingProjectPackageService());
                    var firstScheduler = new RecordingPeriodicTaskSchedulerFactory();
                    var secondScheduler = new RecordingPeriodicTaskSchedulerFactory();
                    using var first = CreateController(firstHost, firstScheduler, root, "first");
                    using var second = CreateController(secondHost, secondScheduler, root, "second");
                    Assert.True(first.OpenProjectAsync(project).GetAwaiter().GetResult());
                    Assert.True(second.OpenProjectAsync(project).GetAwaiter().GetResult());

                    first.MarkDirty();
                    second.MarkDirty();
                    firstScheduler.Scheduler.InvokeCallbackAsync().GetAwaiter().GetResult();
                    secondScheduler.Scheduler.InvokeCallbackAsync().GetAwaiter().GetResult();
                    string firstSnapshot = firstService.LastSaveFilePath!;
                    string secondSnapshot = secondService.LastSaveFilePath!;
                    Assert.NotEqual(firstSnapshot, secondSnapshot);
                    Assert.True(File.Exists(firstSnapshot));
                    Assert.True(File.Exists(secondSnapshot));

                    first.RecoverLatestAutoSaveAsync().GetAwaiter().GetResult();
                    Assert.Equal(project, firstService.LastLoadFilePath);
                    firstHost.SaveSessionDialogPath = project;
                    first.SaveSessionAsync().GetAwaiter().GetResult();
                    Assert.False(File.Exists(firstSnapshot));
                    Assert.True(File.Exists(secondSnapshot));
                    Assert.True(second.IsDirty);
                }
                finally
                {
                    Directory.Delete(root, recursive: true);
                }
            });
        }

        [Fact]
        public void SaveSessionAsync_CleansUpPreSaveAutoSaveAfterTheFileIsRenamed()
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = CreateTempRoot();
                try
                {
                    // 未保存文档先自动保存（autosave-{id}），随后真正保存：文件名会变成 autosave-{名}-{哈希}，
                    // 保存前那份不能留成孤儿。
                    var service = new RecordingSessionService();
                    var host = new RecordingSessionHost(service, new RecordingRecentProjectService(), new RecordingProjectPackageService()) { HasContent = true };
                    var scheduler = new RecordingPeriodicTaskSchedulerFactory();

                    using var controller = CreateController(host, scheduler, rootPath, "document-one");

                    scheduler.Scheduler.InvokeCallbackAsync().GetAwaiter().GetResult();
                    string preSaveAutoSave = service.LastSaveFilePath!;
                    Assert.True(File.Exists(preSaveAutoSave));

                    host.SaveSessionDialogPath = Path.Combine(rootPath, "saved.ivsession");
                    controller.SaveSessionAsync().GetAwaiter().GetResult();

                    Assert.False(File.Exists(preSaveAutoSave), $"保存前的自动保存必须被清理：{preSaveAutoSave}");
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        /// <summary>
        /// 模拟"上一次运行崩溃"：索引里留一条登记，但所有者心跳早已过期。
        /// Chinese: 正常关闭会清理所有权，所以"重启后发现遗留文档"的前提是上次**非正常结束**——
        /// 这里直接改写索引文件来构造这个状态（不经过控制器，避免引入正常退出路径）。
        /// </summary>
        private static void WriteStaleHeartbeatEntry(string rootPath, string autoSaveFilePath, string ownerInstanceId)
        {
            string indexFilePath = Path.Combine(rootPath, "Autosave", "recovery-index.json");
            Directory.CreateDirectory(Path.GetDirectoryName(indexFilePath)!);
            var index = new ImageViewerRecoveryIndex(Path.Combine(rootPath, "Autosave"));
            index.Register(
                autoSaveFilePath,
                DateTimeOffset.UtcNow.AddMinutes(-10),
                ownerInstanceId,
                DateTimeOffset.UtcNow.AddMinutes(-10));
            Assert.True(File.Exists(indexFilePath));
        }

        private static string CreateTempRoot()
        {
            string rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            return rootPath;
        }

        private static ImageViewerSessionController CreateController(
            RecordingSessionHost host,
            RecordingPeriodicTaskSchedulerFactory schedulerFactory,
            string rootPath)
        {
            return new ImageViewerSessionController(
                CreateDependencies(host),
                schedulerFactory,
                new LocalAppDataImageViewerSessionStoragePolicy(rootPath, TimeSpan.FromSeconds(5)));
        }

        private static ImageViewerSessionController CreateController(
            RecordingSessionHost host,
            RecordingPeriodicTaskSchedulerFactory schedulerFactory,
            string rootPath,
            string documentId)
        {
            return new ImageViewerSessionController(
                CreateDependencies(host),
                schedulerFactory,
                new LocalAppDataImageViewerSessionStoragePolicy(rootPath, TimeSpan.FromSeconds(5)),
                documentId);
        }

        [Fact]
        public void CaptureSnapshot_DetachesRoiPayloadFromTheLiveCollection()
        {
            var liveRois = new List<RoiBase> { new CircleRoi { Label = "live", Center = new PointD(1, 2), Radius = 3 } };
            var host = new RecordingSessionHost(
                new RecordingSessionService(),
                new RecordingRecentProjectService(),
                new RecordingProjectPackageService())
            {
                AllRois = liveRois,
                PixelSize = 0.5,
                PhysicalUnit = "mm",
                CurrentImagePath = "source.png"
            };
            ImageViewerSessionControllerDependencies dependencies = CreateDependencies(host);

            ImageViewerPersistenceSnapshot snapshot = dependencies.Persistence.CaptureSnapshot();

            RoiPersistenceData captured = Assert.Single(snapshot.RoiDocument.Items);
            Assert.Equal("circle", captured.Type);
            Assert.Equal("live", captured.Label);

            // 快照之后编辑器继续改动活集合：新增一个 ROI，并改掉已有的半径。
            liveRois.Add(new CircleRoi { Label = "added-later" });
            ((CircleRoi)liveRois[0]).Radius = 42;

            Assert.Single(snapshot.RoiDocument.Items);
            Assert.Equal("live", snapshot.RoiDocument.Items[0].Label);

            // 重新采集才会看到新增项——说明差异来自真正的采集，而不是快照还引用着活集合。
            ImageViewerPersistenceSnapshot second = dependencies.Persistence.CaptureSnapshot();
            Assert.Equal(2, second.RoiDocument.Items.Count);
        }

        [Fact]
        public void CaptureSnapshot_BakesMeasurementContextIntoTheDocument()
        {
            var host = new RecordingSessionHost(
                new RecordingSessionService(),
                new RecordingRecentProjectService(),
                new RecordingProjectPackageService())
            {
                AllRois = [new CircleRoi()],
                PixelSize = 0.25,
                PhysicalUnit = "mm"
            };
            ImageViewerSessionControllerDependencies dependencies = CreateDependencies(host);

            ImageViewerPersistenceSnapshot snapshot = dependencies.Persistence.CaptureSnapshot();

            host.PixelSize = 9.0;
            host.PhysicalUnit = "um";

            Assert.Equal(0.25, snapshot.RoiDocument.PixelSize);
            Assert.Equal("mm", snapshot.RoiDocument.PhysicalUnit);
        }

        [Fact]
        public void AutoSave_WithoutProjectPath_TwoDocumentsUseDistinctFiles()
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = CreateTempRoot();
                try
                {
                    // 两个未保存文档共用同一个 Autosave 目录（默认策略），必须写各自的文件。
                    var firstService = new RecordingSessionService();
                    var secondService = new RecordingSessionService();
                    var firstHost = new RecordingSessionHost(firstService, new RecordingRecentProjectService(), new RecordingProjectPackageService()) { HasContent = true };
                    var secondHost = new RecordingSessionHost(secondService, new RecordingRecentProjectService(), new RecordingProjectPackageService()) { HasContent = true };
                    var firstScheduler = new RecordingPeriodicTaskSchedulerFactory();
                    var secondScheduler = new RecordingPeriodicTaskSchedulerFactory();

                    using var firstController = CreateController(firstHost, firstScheduler, rootPath, "document-one");
                    using var secondController = CreateController(secondHost, secondScheduler, rootPath, "document-two");

                    firstScheduler.Scheduler.InvokeCallbackAsync().GetAwaiter().GetResult();
                    secondScheduler.Scheduler.InvokeCallbackAsync().GetAwaiter().GetResult();

                    Assert.NotNull(firstService.LastSaveFilePath);
                    Assert.NotNull(secondService.LastSaveFilePath);
                    Assert.NotEqual(
                        Path.GetFileName(firstService.LastSaveFilePath),
                        Path.GetFileName(secondService.LastSaveFilePath));
                    Assert.Equal("autosave-document-one.ivsession", Path.GetFileName(firstService.LastSaveFilePath));
                    Assert.Equal("autosave-document-two.ivsession", Path.GetFileName(secondService.LastSaveFilePath));
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Fact]
        public void RecoverLatestAutoSave_DoesNotAdoptAnotherDocumentsAutoSave()
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = CreateTempRoot();
                try
                {
                    string autoSaveDirectory = Path.Combine(rootPath, "Autosave");
                    Directory.CreateDirectory(autoSaveDirectory);
                    // 另一个窗口（document-other）留下的自动保存，内容非空。
                    string foreignAutoSave = Path.Combine(autoSaveDirectory, "autosave-document-other.ivsession");
                    File.WriteAllText(foreignAutoSave, "foreign");

                    var sessionService = new RecordingSessionService();
                    var host = new RecordingSessionHost(sessionService, new RecordingRecentProjectService(), new RecordingProjectPackageService());
                    var schedulerFactory = new RecordingPeriodicTaskSchedulerFactory();

                    using var controller = CreateController(host, schedulerFactory, rootPath, "document-mine");

                    // 本窗口只有别的文档的自动保存：不应把它当成自己的恢复候选。
                    Assert.False(controller.HasRecoverySnapshot);
                    Assert.Null(sessionService.LastLoadFilePath);

                    controller.RecoverLatestAutoSaveAsync().GetAwaiter().GetResult();

                    Assert.Null(sessionService.LastLoadFilePath);
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Fact]
        public void RecoverLatestAutoSave_AcceptsLegacySharedAutoSaveWhenNoOwnCandidateExists()
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = CreateTempRoot();
                try
                {
                    string autoSaveDirectory = Path.Combine(rootPath, "Autosave");
                    Directory.CreateDirectory(autoSaveDirectory);
                    // 旧版本遗留的共享文件名（所有未保存文档都写它）。
                    string legacyAutoSave = Path.Combine(autoSaveDirectory, "autosave.ivsession");
                    File.WriteAllText(legacyAutoSave, "legacy");

                    // ApplySession 会校验图片存在，给一个真实文件。
                    string sourceImage = Path.Combine(rootPath, "source.png");
                    File.WriteAllText(sourceImage, "image");

                    var sessionService = new RecordingSessionService
                    {
                        LoadResult = new ImageViewerSessionData(
                            sourceImage,
                            DateTimeOffset.UtcNow,
                            null,
                            [],
                            1.0,
                            "px",
                            1.0,
                            0,
                            0,
                            null)
                    };
                    var host = new RecordingSessionHost(sessionService, new RecordingRecentProjectService(), new RecordingProjectPackageService());
                    var schedulerFactory = new RecordingPeriodicTaskSchedulerFactory();

                    using var controller = CreateController(host, schedulerFactory, rootPath, "document-mine");

                    // 升级兜底：没有自己的候选时，仍认旧的共享文件。
                    Assert.True(controller.HasRecoverySnapshot);

                    controller.RecoverLatestAutoSaveAsync().GetAwaiter().GetResult();

                    Assert.Equal(legacyAutoSave, sessionService.LastLoadFilePath);
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Fact]
        public void SaveRecipeAsync_WritesDeliverablePlanWithoutBakingInImagePath()
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = Path.Combine(Path.GetTempPath(), "iv-recipe-save-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(rootPath);
                try
                {
                    var host = new RecordingSessionHost(new RecordingSessionService(), new RecordingRecentProjectService(), new RecordingProjectPackageService());
                    host.SaveRecipeDialogPath = Path.Combine(rootPath, "plan.ivrecipe");
                    host.AllRois =
                    [
                        new LineMeasureRoi
                        {
                            Label = "总长",
                            P1 = new PointD(0, 0),
                            P2 = new PointD(100, 0),
                            Tolerance = new MeasurementTolerance { Nominal = 12.5, TolerancePlus = 0.1, ToleranceMinus = 0.1 }
                        }
                    ];

                    using var controller = CreateController(host, new RecordingPeriodicTaskSchedulerFactory(), rootPath, "document-mine");
                    controller.SaveRecipeAsync().GetAwaiter().GetResult();

                    Assert.True(File.Exists(host.SaveRecipeDialogPath));
                    MeasurementRecipeDocument recipe = MeasurementRecipeService.LoadFromFile(host.SaveRecipeDialogPath);

                    Assert.Single(recipe.Items);
                    Assert.Equal("总长", recipe.Items[0].DisplayName);
                    Assert.Equal(12.5, recipe.Items[0].Tolerance!.Nominal);
                    Assert.NotNull(recipe.Items[0].Template);

                    // 配方必须"可交付"：不能把本文档的图像路径烘进测量方案，否则换一张图就失效。
                    Assert.DoesNotContain(host.CurrentImagePath ?? "never", File.ReadAllText(host.SaveRecipeDialogPath));
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Fact]
        public void SaveRecipeAsync_WithoutRois_WarnsAndWritesNothing()
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = Path.Combine(Path.GetTempPath(), "iv-recipe-empty-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(rootPath);
                try
                {
                    var host = new RecordingSessionHost(new RecordingSessionService(), new RecordingRecentProjectService(), new RecordingProjectPackageService());
                    host.SaveRecipeDialogPath = Path.Combine(rootPath, "empty.ivrecipe");
                    host.AllRois = [];

                    using var controller = CreateController(host, new RecordingPeriodicTaskSchedulerFactory(), rootPath, "document-mine");
                    controller.SaveRecipeAsync().GetAwaiter().GetResult();

                    // 写一个空配方比不写更糟：操作员会以为方案已保存。
                    Assert.False(File.Exists(host.SaveRecipeDialogPath));
                    Assert.Contains(UiText.Get("StatusRecipeNoRois"), host.StatusHints);
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Fact]
        public void LoadRecipeAsync_RestoresRoisToleranceScaleAndReportsReadiness()
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = Path.Combine(Path.GetTempPath(), "iv-recipe-load-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(rootPath);
                try
                {
                    var host = new RecordingSessionHost(new RecordingSessionService(), new RecordingRecentProjectService(), new RecordingProjectPackageService());
                    string recipePath = Path.Combine(rootPath, "loaded.ivrecipe");
                    var qualityProfile = ImageAnalysisQualityProfile.Default with
                    {
                        PassConfidence = 0.82,
                        AutomaticCircleMinimumValidCalipers = 36
                    };
                    MeasurementRecipeDocument recipe = MeasurementRecipeService.Create(
                        "配方",
                        null,
                        [
                            new LineMeasureRoi
                            {
                                Label = "总长",
                                P1 = new PointD(0, 0),
                                P2 = new PointD(100, 0),
                                Tolerance = new MeasurementTolerance { Nominal = 12.5, TolerancePlus = 0.1, ToleranceMinus = 0.1 }
                            }
                        ],
                        0.05,
                        "mm",
                        host.PluginRegistry,
                        calibration: new CameraCalibration { K1 = 0.01, NormalizationRadius = 500, CalibrationId = "C-9" },
                        qualityProfile: qualityProfile);
                    MeasurementRecipeService.SaveToFile(recipePath, recipe);

                    host.OpenRecipeDialogPath = recipePath;
                    using var controller = CreateController(host, new RecordingPeriodicTaskSchedulerFactory(), rootPath, "document-mine");

                    controller.LoadRecipeAsync().GetAwaiter().GetResult();

                    Assert.NotNull(host.LastReplacedRois);
                    Assert.Single(host.LastReplacedRois!);
                    Assert.Equal(0.05, host.PixelSize);
                    Assert.Equal("mm", host.PhysicalUnit);
                    Assert.Equal("C-9", host.Calibration?.CalibrationId);
                    Assert.Equal(qualityProfile, host.QualityProfile);
                    Assert.True(controller.IsDirty);
                    Assert.Equal(1, host.ClearUndoHistoryCount);

                    // "载入配方"必须当场给出可判定性，而不是让操作员以为一切就绪。
                    Assert.NotEmpty(host.StatusHints);
                    Assert.Equal(1, host.DrawRoisCallCount);
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Fact]
        public void LoadAlignedRecipeAsync_TransformsRoisAndRebasesReferenceDatums()
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = Path.Combine(Path.GetTempPath(), "iv-recipe-align-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(rootPath);
                try
                {
                    var host = new RecordingSessionHost(new RecordingSessionService(), new RecordingRecentProjectService(), new RecordingProjectPackageService())
                    {
                        CurrentRecipeDatumPoints =
                        [
                            new RecipeDatumPoint { Name = "D1", X = 300, Y = 400 },
                            new RecipeDatumPoint { Name = "D2", X = 300, Y = 500 }
                        ]
                    };
                    string recipePath = Path.Combine(rootPath, "aligned.ivrecipe");
                    var qualityProfile = ImageAnalysisQualityProfile.Default with
                    {
                        PassValidRatio = 0.86,
                        PassResidualRms = 0.65
                    };
                    MeasurementRecipeDocument recipe = MeasurementRecipeService.Create(
                        "配方",
                        null,
                        [new LineMeasureRoi { P1 = new PointD(120, 100), P2 = new PointD(140, 100) }],
                        0.2,
                        "mm",
                        host.PluginRegistry,
                        datumPoints:
                        [
                            new RecipeDatumPoint { Name = "D1", X = 100, Y = 100 },
                            new RecipeDatumPoint { Name = "D2", X = 200, Y = 100 }
                        ], qualityProfile: qualityProfile);
                    MeasurementRecipeService.SaveToFile(recipePath, recipe);
                    host.OpenRecipeDialogPath = recipePath;

                    using var controller = CreateController(host, new RecordingPeriodicTaskSchedulerFactory(), rootPath, "document-align");
                    controller.LoadAlignedRecipeAsync().GetAwaiter().GetResult();

                    Assert.NotNull(host.LastReplacedRois);
                    var line = Assert.IsType<LineMeasureRoi>(Assert.Single(host.LastReplacedRois!));
                    Assert.Equal(new PointD(300, 420), line.P1);
                    Assert.Equal(new PointD(300, 440), line.P2);
                    Assert.Equal(0.2, host.PixelSize);
                    Assert.Equal("mm", host.PhysicalUnit);
                    Assert.Equal(qualityProfile, host.QualityProfile);
                    Assert.Equal(300, host.RecipeDatumPoints[0].X);
                    Assert.Equal(400, host.RecipeDatumPoints[0].Y);
                    Assert.Contains(host.StatusHints, hint => hint.Contains(UiText.Get("StatusRecipeManualAlignmentApplied"), StringComparison.Ordinal));

                    host.SaveRecipeDialogPath = Path.Combine(rootPath, "rebased.ivrecipe");
                    controller.SaveRecipeAsync().GetAwaiter().GetResult();
                    MeasurementRecipeDocument rebased = MeasurementRecipeService.LoadFromFile(host.SaveRecipeDialogPath);
                    Assert.Equal(300, rebased.DatumPoints[0].X);
                    Assert.Equal(400, rebased.DatumPoints[0].Y);
                    Assert.Equal(300, rebased.DatumPoints[1].X);
                    Assert.Equal(500, rebased.DatumPoints[1].Y);
                    Assert.Equal(qualityProfile, rebased.QualityProfile);
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Fact]
        public void LoadAlignedRecipeAsync_UnsupportedGeometryLeavesExistingStateUnchanged()
        {
            WpfTestRunner.Run(() =>
            {
                string rootPath = Path.Combine(Path.GetTempPath(), "iv-recipe-align-invalid-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(rootPath);
                try
                {
                    var oldRoi = new LineMeasureRoi { P1 = new PointD(1, 2), P2 = new PointD(3, 4) };
                    var host = new RecordingSessionHost(new RecordingSessionService(), new RecordingRecentProjectService(), new RecordingProjectPackageService())
                    {
                        AllRois = [oldRoi],
                        CurrentRecipeDatumPoints =
                        [
                            new RecipeDatumPoint { Name = "D1", X = 30, Y = 40 },
                            new RecipeDatumPoint { Name = "D2", X = 30, Y = 50 }
                        ]
                    };
                    string recipePath = Path.Combine(rootPath, "unsupported.ivrecipe");
                    MeasurementRecipeService.SaveToFile(recipePath, new MeasurementRecipeDocument
                    {
                        Version = MeasurementRecipeDocument.CurrentVersion,
                        PixelSize = 0.5,
                        PhysicalUnit = "mm",
                        DatumPoints =
                        [
                            new RecipeDatumPoint { Name = "D1", X = 10, Y = 20 },
                            new RecipeDatumPoint { Name = "D2", X = 20, Y = 20 }
                        ],
                        Items =
                        {
                            new MeasurementRecipeItem
                            {
                                Key = "plugin#0",
                                RoiTypeKey = "plugin-roi",
                                Template = new RoiPersistenceData { Type = "plugin-roi", Position = new RoiPersistencePoint { X = 4, Y = 5 } }
                            }
                        }
                    });
                    host.OpenRecipeDialogPath = recipePath;

                    using var controller = CreateController(host, new RecordingPeriodicTaskSchedulerFactory(), rootPath, "document-align-invalid");
                    host.CurrentRecipeDatumPoints =
                    [
                        new RecipeDatumPoint { Name = "D1", X = 30, Y = 40 },
                        new RecipeDatumPoint { Name = "D2", X = 30, Y = 40 }
                    ];
                    controller.LoadAlignedRecipeAsync().GetAwaiter().GetResult();
                    Assert.Null(host.LastReplacedRois);
                    Assert.Same(oldRoi, Assert.Single(host.AllRois));
                    Assert.Equal(1.0, host.PixelSize);

                    host.CurrentRecipeDatumPoints =
                    [
                        new RecipeDatumPoint { Name = "D1", X = 30, Y = 40 },
                        new RecipeDatumPoint { Name = "D2", X = 30, Y = 50 }
                    ];
                    controller.LoadAlignedRecipeAsync().GetAwaiter().GetResult();

                    Assert.Null(host.LastReplacedRois);
                    Assert.Same(oldRoi, Assert.Single(host.AllRois));
                    Assert.Equal(1.0, host.PixelSize);
                    Assert.Contains(host.StatusHints, hint => hint.Contains(UiText.Get("StatusRecipeAlignmentDatumsRequired"), StringComparison.Ordinal));
                    Assert.Contains(host.StatusHints, hint => hint.Contains(UiText.Get("StatusRecipeAlignmentFailed").Split('{')[0], StringComparison.Ordinal));
                }
                finally
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            });
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void RecipeLoad_HandlesMissingAndInvalidQualityProfile(bool aligned, bool invalidProfile)
        {
            WpfTestRunner.Run(() =>
            {
                string root = CreateTempRoot();
                try
                {
                    var originalRoi = new CircleRoi { Center = new PointD(1, 2), Radius = 3 };
                    var existingProfile = ImageAnalysisQualityProfile.Default with { PassConfidence = 0.91 };
                    var host = new RecordingSessionHost(new RecordingSessionService(), new RecordingRecentProjectService(), new RecordingProjectPackageService())
                    {
                        AllRois = [originalRoi],
                        PixelSize = 0.25,
                        QualityProfile = existingProfile,
                        CurrentRecipeDatumPoints =
                        [
                            new RecipeDatumPoint { X = 30, Y = 40 },
                            new RecipeDatumPoint { X = 40, Y = 40 }
                        ],
                        OpenRecipeDialogPath = Path.Combine(root, "recipe.ivrecipe")
                    };

                    // Write the external file directly, so serializer validation cannot hide an invalid input.
                    string qualityField = invalidProfile ? "\"QualityProfile\": { \"PassConfidence\": 1.25 }," : string.Empty;
                    File.WriteAllText(host.OpenRecipeDialogPath, $$"""
                        {
                          "Version": 1,
                          "PixelSize": 0.5,
                          "PhysicalUnit": "mm",
                          {{qualityField}}
                          "DatumPoints": [{ "X": 10, "Y": 20 }, { "X": 20, "Y": 20 }],
                          "Items": [{ "Key": "circle#0", "Template": { "Type": "circle", "Center": { "X": 12, "Y": 22 }, "Radius": 2 } }]
                        }
                        """);

                    using var controller = CreateController(host, new RecordingPeriodicTaskSchedulerFactory(), root);
                    (aligned ? controller.LoadAlignedRecipeAsync() : controller.LoadRecipeAsync()).GetAwaiter().GetResult();

                    Assert.Same(existingProfile, host.QualityProfile);
                    if (invalidProfile)
                    {
                        var error = Assert.IsType<System.Text.Json.JsonException>(Assert.Single(host.Errors).Exception);
                        Assert.Equal(nameof(ImageAnalysisQualityProfile.PassConfidence),
                            Assert.IsType<ArgumentOutOfRangeException>(error.InnerException).ParamName);
                        Assert.Same(originalRoi, Assert.Single(host.AllRois));
                        Assert.Equal(0.25, host.PixelSize);
                        Assert.Null(host.LastReplacedRois);
                        Assert.Equal(0, host.DrawRoisCallCount);
                    }
                    else
                    {
                        Assert.Empty(host.Errors);
                        Assert.Equal(0.5, host.PixelSize);
                        var loaded = Assert.IsType<CircleRoi>(Assert.Single(host.LastReplacedRois!));
                        Assert.Equal(aligned ? new PointD(32, 42) : new PointD(12, 22), loaded.Center);
                    }
                }
                finally
                {
                    Directory.Delete(root, recursive: true);
                }
            });
        }

        [Fact]
        public void LoadRecipeAsync_MissingPluginRejectsTheEntireRecipe()
        {
            WpfTestRunner.Run(() =>
            {
                string root = CreateTempRoot();
                try
                {
                    var original = new CircleRoi { Center = new PointD(1, 2), Radius = 3 };
                    var host = new RecordingSessionHost(new RecordingSessionService(), new RecordingRecentProjectService(), new RecordingProjectPackageService())
                    {
                        AllRois = [original],
                        OpenRecipeDialogPath = Path.Combine(root, "plugin.ivrecipe"),
                        PixelSize = 0.25
                    };
                    MeasurementRecipeDocument recipe = MeasurementRecipeService.Create("mixed", null,
                        [new CircleRoi { Center = new PointD(30, 40), Radius = 5 }], 0.5, "mm", host.PluginRegistry,
                        qualityProfile: ImageAnalysisQualityProfile.Default with { PassConfidence = 0.8 });
                    recipe.Items.Add(new MeasurementRecipeItem
                    {
                        Key = "custom#0",
                        Template = new RoiPersistenceData { Type = "missing-plugin-circle" }
                    });
                    MeasurementRecipeService.SaveToFile(host.OpenRecipeDialogPath, recipe);

                    using var controller = CreateController(host, new RecordingPeriodicTaskSchedulerFactory(), root);
                    controller.MarkDirty();
                    controller.LoadRecipeAsync().GetAwaiter().GetResult();

                    Assert.Same(original, Assert.Single(host.AllRois));
                    Assert.Equal(0.25, host.PixelSize);
                    Assert.Equal(ImageAnalysisQualityProfile.Default, host.QualityProfile);
                    Assert.Equal(0, host.UnsavedChangesPromptCount);
                    Assert.Equal(0, host.ClearUndoHistoryCount);
                    Assert.Contains(host.StatusHints, hint => hint.Contains("missing-plugin-circle", StringComparison.Ordinal));
                }
                finally
                {
                    Directory.Delete(root, recursive: true);
                }
            });
        }

        [Theory]
        [InlineData(false, "cancel")]
        [InlineData(true, "cancel")]
        [InlineData(false, "image")]
        [InlineData(true, "image")]
        [InlineData(false, "edit")]
        [InlineData(true, "edit")]
        public void RecipeLoad_PreservesCurrentStateWhenCanceledOrTargetChanges(bool aligned, string interruption)
        {
            WpfTestRunner.Run(() =>
            {
                string root = CreateTempRoot();
                try
                {
                    var original = new CircleRoi { Center = new PointD(1, 2), Radius = 3 };
                    var host = new RecordingSessionHost(new RecordingSessionService(), new RecordingRecentProjectService(), new RecordingProjectPackageService())
                    {
                        AllRois = [original],
                        OpenRecipeDialogPath = Path.Combine(root, "candidate.ivrecipe"),
                        CurrentRecipeDatumPoints = [new() { X = 30, Y = 40 }, new() { X = 40, Y = 40 }],
                        UnsavedChangesDecision = MessageBoxResult.Cancel
                    };
                    MeasurementRecipeDocument recipe = MeasurementRecipeService.Create("candidate", null,
                        [new CircleRoi { Center = new PointD(30, 40), Radius = 5 }], 0.5, "mm", host.PluginRegistry,
                        datumPoints: [new() { X = 10, Y = 20 }, new() { X = 20, Y = 20 }]);
                    MeasurementRecipeService.SaveToFile(host.OpenRecipeDialogPath, recipe);

                    using var controller = CreateController(host, new RecordingPeriodicTaskSchedulerFactory(), root);
                    if (interruption == "cancel")
                    {
                        controller.MarkDirty();
                    }
                    else
                    {
                        // Simulate a change after preparation has finished but before its apply callback runs.
                        host.BeforeApplyDocumentLoad = () =>
                        {
                            if (interruption == "image")
                            {
                                host.CurrentImageIdentity = new object();
                            }
                            else
                            {
                                original.Radius = 8;
                                controller.MarkDirty();
                            }
                        };
                    }

                    (aligned ? controller.LoadAlignedRecipeAsync() : controller.LoadRecipeAsync()).GetAwaiter().GetResult();

                    Assert.Same(original, Assert.Single(host.AllRois));
                    Assert.Equal(interruption == "edit" ? 8 : 3, original.Radius);
                    Assert.Equal(1, host.PixelSize);
                    Assert.Equal(0, host.ClearUndoHistoryCount);
                    Assert.Empty(host.Errors);
                    if (interruption == "cancel")
                    {
                        Assert.Equal(1, host.UnsavedChangesPromptCount);
                        Assert.True(controller.IsDirty);
                    }
                    else
                    {
                        Assert.Contains(UiText.Get("StatusRecipeTargetChanged"), host.StatusHints);
                    }
                }
                finally
                {
                    Directory.Delete(root, recursive: true);
                }
            });
        }

        private static ImageViewerSessionControllerDependencies CreateDependencies(RecordingSessionHost host)
        {
            return new ImageViewerSessionControllerDependencies
            {
                Persistence = new ImageViewerSessionPersistenceWorkflow
                {
                    ShowSaveSessionDialog = () => host.SaveSessionDialogPath,
                    ConfirmUnsavedChanges = () =>
                    {
                        host.UnsavedChangesPromptCount++;
                        return host.UnsavedChangesDecision;
                    },
                    ShowOpenSessionDialog = () => host.OpenSessionDialogPath,
                    ShowSaveProjectPackageDialog = () => host.SaveProjectPackageDialogPath,
                    ShowSaveRecipeDialog = () => host.SaveRecipeDialogPath,
                    ShowOpenRecipeDialog = () => host.OpenRecipeDialogPath,
                    SessionService = host.SessionService,
                    RecentProjectService = host.RecentProjectService,
                    ProjectPackageService = host.ProjectPackageService,
                    GetPluginRegistry = () => host.PluginRegistry,
                    GetAllRois = () => host.AllRois,
                    GetPixelSize = () => host.PixelSize,
                    SetPixelSize = value => host.PixelSize = value,
                    GetPhysicalUnit = () => host.PhysicalUnit,
                    SetPhysicalUnit = value => host.PhysicalUnit = value,
                    GetCalibration = () => host.Calibration,
                    SetCalibration = value => host.Calibration = value,
                    GetQualityProfile = () => host.QualityProfile,
                    SetQualityProfile = value => host.QualityProfile = value,
                    GetWorkpieceCoordinateSystem = () => host.CoordinateSystem,
                    SetWorkpieceCoordinateSystem = value => host.CoordinateSystem = value,
                    GetRecipeDatumPoints = () => host.RecipeDatumPoints,
                    GetCurrentRecipeDatumPoints = () => host.CurrentRecipeDatumPoints,
                    GetCurrentImageIdentity = () => host.CurrentImageIdentity,
                    SetRecipeDatumPoints = value => host.RecipeDatumPoints = value.ToArray(),
                    SetCurrentRecipeDatumPoints = value => host.CurrentRecipeDatumPoints = value.ToArray(),
                    GetCurrentViewportState = () => host.CurrentViewportState,
                    TryGetCurrentImagePath = () => host.CurrentImagePath,
                    RunDocumentLoadAsync = async prepare =>
                    {
                        Action? apply = await prepare(CancellationToken.None, new Progress<double>()).ConfigureAwait(false);
                        if (apply != null)
                        {
                            host.BeforeApplyDocumentLoad?.Invoke();
                            apply();
                        }
                        return apply != null;
                    },
                    PrepareImageAsync = (path, _, _, _) => Task.FromResult<Action>(() => host.LoadImageFromFile(path, false)),
                    ClearImage = () => host.CurrentImagePath = null,
                    ReplaceAllRois = host.ReplaceAllRois,
                    ApplyViewportState = host.ApplyViewportState,
                    DrawRois = host.DrawRois,
                    ShowNonCriticalError = host.ShowNonCriticalError,
                    ShowWarning = host.ShowWarning,
                    ShowStatusHint = (message, _) => host.StatusHints.Add(message),
                    ClearUndoHistory = () => host.ClearUndoHistoryCount++,
                    UpdateContextMenuState = host.UpdateContextMenuState
                },
                AutoSave = new ImageViewerAutoSaveWorkflow
                {
                    Dispatcher = host.Dispatcher,
                    HasContent = () => host.HasContent,
                    LogNonCriticalError = host.LogNonCriticalError,
                    ShowStatusHint = (message, _) => host.StatusHints.Add(message)
                }
            };
        }

        private sealed class RecordingSessionHost
        {
            public WorkpieceCoordinateSystem? CoordinateSystem { get; set; }

            public RecordingSessionHost(
                IImageViewerSessionService sessionService,
                IImageViewerRecentProjectService recentProjectService,
                IImageViewerProjectPackageService projectPackageService)
            {
                SessionService = sessionService;
                RecentProjectService = recentProjectService;
                ProjectPackageService = projectPackageService;
            }

            public Dispatcher Dispatcher { get; } = Dispatcher.CurrentDispatcher;

            public string? SaveSessionDialogPath { get; set; }
            public MessageBoxResult UnsavedChangesDecision { get; set; } = MessageBoxResult.No;
            public int UnsavedChangesPromptCount { get; set; }

            public string? OpenSessionDialogPath { get; set; }

            public string? SaveProjectPackageDialogPath { get; set; }

            public string? SaveRecipeDialogPath { get; set; }

            public string? OpenRecipeDialogPath { get; set; }

            public IImageViewerSessionService SessionService { get; }

            public IImageViewerRecentProjectService RecentProjectService { get; }

            public IImageViewerProjectPackageService ProjectPackageService { get; }

            public RoiPluginRegistry PluginRegistry { get; } = RoiPluginRegistry.CreateBuiltIn();

            public IReadOnlyList<RoiBase> AllRois { get; set; } = [];

            public IReadOnlyList<RecipeDatumPoint> RecipeDatumPoints { get; set; } = [];

            public IReadOnlyList<RecipeDatumPoint> CurrentRecipeDatumPoints { get; set; } = [];

            public object CurrentImageIdentity { get; set; } = new object();
            public Action? BeforeApplyDocumentLoad { get; set; }

            // 与真实控件的默认标定一致（像素尺寸必须为有限正值，快照阶段就会校验）。
            public double PixelSize { get; set; } = 1.0;

            public string PhysicalUnit { get; set; } = "px";

            public CameraCalibration? Calibration { get; set; }
            public ImageAnalysisQualityProfile QualityProfile { get; set; } = ImageAnalysisQualityProfile.Default;

            public ImageViewerViewportState CurrentViewportState { get; set; } = new(1, 0, 0);

            public bool HasContent { get; set; } = true;

            public string? CurrentImagePath { get; set; }

            public string? LastLoadedImagePath { get; private set; }

            public bool LastLoadImageFitToView { get; private set; }

            public IReadOnlyList<RoiBase>? LastReplacedRois { get; private set; }

            public ImageViewerViewportState? LastAppliedViewportState { get; private set; }

            public int DrawRoisCallCount { get; private set; }

            public int UpdateContextMenuStateCount { get; private set; }

            public List<(string Title, string Message)> Warnings { get; } = [];

            public List<(string Title, string Message, Exception Exception)> Errors { get; } = [];

            public List<string> StatusHints { get; } = [];

            public int ClearUndoHistoryCount { get; set; }

            public List<(string Context, Exception Exception)> LoggedErrors { get; } = [];

            public string? ShowSaveSessionDialog() => SaveSessionDialogPath;

            public string? ShowOpenSessionDialog() => OpenSessionDialogPath;

            public string? ShowSaveProjectPackageDialog() => SaveProjectPackageDialogPath;

            public string? TryGetCurrentImagePath() => CurrentImagePath;

            public void LoadImageFromFile(string filePath, bool fitToView)
            {
                LastLoadedImagePath = filePath;
                LastLoadImageFitToView = fitToView;
            }

            public void ReplaceAllRois(IReadOnlyList<RoiBase> rois)
            {
                LastReplacedRois = rois;
                AllRois = rois;
            }

            public void ApplyViewportState(ImageViewerViewportState state)
            {
                LastAppliedViewportState = state;
            }

            public void DrawRois()
            {
                DrawRoisCallCount++;
            }

            public void ShowNonCriticalError(string title, string message, Exception ex)
            {
                Errors.Add((title, message, ex));
            }

            public void LogNonCriticalError(string context, Exception ex)
            {
                LoggedErrors.Add((context, ex));
            }

            public void ShowWarning(string title, string message)
            {
                Warnings.Add((title, message));
            }

            public void UpdateContextMenuState()
            {
                UpdateContextMenuStateCount++;
            }
        }

        private sealed class RecordingSessionService : IImageViewerSessionService
        {
            public string? LastSaveFilePath { get; private set; }

            public ImageViewerPersistenceSnapshot? LastSavedSnapshot { get; private set; }

            public RoiPluginRegistry? LastSavePluginRegistry { get; private set; }

            public string? LastLoadFilePath { get; private set; }

            public RoiPluginRegistry? LastLoadPluginRegistry { get; private set; }

            public bool ThrowOnSave { get; set; }

            public Task? AllowAutoSave { get; set; }

            public ImageViewerSessionData LoadResult { get; set; } = new(
                "session",
                DateTimeOffset.UtcNow,
                null,
                [],
                1,
                "px",
                1,
                0,
                0,
                null);

            public void SaveToFile(string filePath, ImageViewerPersistenceSnapshot snapshot, RoiPluginRegistry pluginRegistry)
            {
                throw new NotSupportedException();
            }

            public Task SaveToFileAsync(string filePath, ImageViewerPersistenceSnapshot snapshot, RoiPluginRegistry pluginRegistry, CancellationToken cancellationToken = default)
            {
                if (ThrowOnSave)
                {
                    return Task.FromException(new IOException("disk full"));
                }

                LastSaveFilePath = filePath;
                LastSavedSnapshot = snapshot;
                LastSavePluginRegistry = pluginRegistry;
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                File.WriteAllText(filePath, "session");
                return Task.CompletedTask;
            }

            public async Task SaveRecoveryToFileAsync(string filePath, ImageViewerPersistenceSnapshot snapshot, RoiPluginRegistry pluginRegistry, CancellationToken cancellationToken = default)
            {
                if (AllowAutoSave is { } pending)
                {
                    await pending.WaitAsync(cancellationToken);
                }
                await SaveToFileAsync(filePath, snapshot, pluginRegistry, cancellationToken);
            }

            public string SerializeSession(string? sessionName, ImageViewerPersistenceSnapshot snapshot, RoiPluginRegistry pluginRegistry)
            {
                throw new NotSupportedException();
            }

            public ImageViewerSessionData LoadFromFile(string filePath, RoiPluginRegistry pluginRegistry)
            {
                throw new NotSupportedException();
            }

            public Task<ImageViewerSessionData> LoadFromFileAsync(string filePath, RoiPluginRegistry pluginRegistry, CancellationToken cancellationToken = default)
            {
                LastLoadFilePath = filePath;
                LastLoadPluginRegistry = pluginRegistry;
                return Task.FromResult(LoadResult);
            }

            public ImageViewerSessionData LoadFromJson(string sessionJson, RoiPluginRegistry pluginRegistry, string? sessionBaseDirectory = null)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class RecordingProjectPackageService : IImageViewerProjectPackageService
        {
            public string? LastExportFilePath { get; private set; }

            public RoiPluginRegistry? LastExportPluginRegistry { get; private set; }

            public string? LastLoadFilePath { get; private set; }

            public RoiPluginRegistry? LastLoadPluginRegistry { get; private set; }

            public ImageViewerSessionData LoadResult { get; set; } = new(
                "package",
                DateTimeOffset.UtcNow,
                null,
                [],
                1,
                "px",
                1,
                0,
                0,
                null);

            public Task ExportAsync(string packagePath, ImageViewerPersistenceSnapshot snapshot, RoiPluginRegistry pluginRegistry, CancellationToken cancellationToken = default)
            {
                LastExportFilePath = packagePath;
                LastExportPluginRegistry = pluginRegistry;
                Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
                File.WriteAllText(packagePath, "package");
                return Task.CompletedTask;
            }

            public Task<ImageViewerSessionData> LoadAsync(string packagePath, RoiPluginRegistry pluginRegistry, CancellationToken cancellationToken = default)
            {
                LastLoadFilePath = packagePath;
                LastLoadPluginRegistry = pluginRegistry;
                return Task.FromResult(LoadResult);
            }
        }

        private sealed class RecordingRecentProjectService : IImageViewerRecentProjectService
        {
            private IReadOnlyList<RecentImageViewerProject> _items = [];

            public IReadOnlyList<RecentImageViewerProject>? LastSavedItems { get; private set; }

            public void Seed(params RecentImageViewerProject[] items)
            {
                _items = items;
            }

            public IReadOnlyList<RecentImageViewerProject> Load(string filePath, int maxCount = 10)
            {
                return _items;
            }

            public void Save(string filePath, IEnumerable<RecentImageViewerProject> items)
            {
                _items = items.ToArray();
                LastSavedItems = _items;
            }

            public IReadOnlyList<RecentImageViewerProject> Touch(IEnumerable<RecentImageViewerProject> items, string filePath, string projectKind, int maxCount = 10)
            {
                return new ImageViewerRecentProjectService().Touch(items, filePath, projectKind, maxCount);
            }
        }

        private sealed class RecordingPeriodicTaskSchedulerFactory : IImageViewerPeriodicTaskSchedulerFactory
        {
            public RecordingPeriodicTaskScheduler Scheduler { get; } = new();

            public IImageViewerPeriodicTaskScheduler Create(Func<Task> callback, Dispatcher dispatcher, DispatcherPriority priority, TimeSpan interval)
            {
                Scheduler.Callback = callback;
                return Scheduler;
            }
        }

        private sealed class RecordingPeriodicTaskScheduler : IImageViewerPeriodicTaskScheduler
        {
            public Func<Task>? Callback { get; set; }

            public bool StartCalled { get; private set; }

            public bool StopCalled { get; private set; }

            public void Start()
            {
                StartCalled = true;
            }

            public void StopScheduling()
            {
                StopCalled = true;
            }

            public Task DrainAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public void Dispose()
            {
            }

            public Task InvokeCallbackAsync()
            {
                return Callback?.Invoke() ?? Task.CompletedTask;
            }
        }
    }
}
