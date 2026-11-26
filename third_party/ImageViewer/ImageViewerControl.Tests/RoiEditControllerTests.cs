using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using ImageViewer.Abstractions;
using ImageViewer.Controls;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.ViewModels;
using Xunit;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// <see cref="RoiEditController"/> 的撤销/重做失败路径测试。
    /// Chinese: 守住"命令抛异常时不崩溃、不误报成功"：控制器应吞掉异常、写日志、给出错误状态提示，
    /// 且不执行后续的界面刷新与"已撤销/已重做"提示。
    /// English: Verifies the controller turns a throwing undo/redo into a log entry plus an error status hint,
    /// without claiming success or crashing the input path.
    /// </summary>
    public class RoiEditControllerTests
    {
        [Fact]
        public void Undo_WhenCommandThrows_LogsAndShowsErrorHintWithoutClaimingSuccess()
        {
            var host = new ControllerHost();
            host.ViewModel.UndoRedo.Execute(new ThrowingOnUndoCommand());

            RoiEditController controller = host.CreateController();

            controller.Undo();

            Assert.Single(host.Logs);
            Assert.Contains(host.StatusHints, hint => hint.Kind == StatusHintKind.Error);
            Assert.DoesNotContain(host.StatusHints, hint => hint.Message == "已撤销");
            Assert.Equal(0, host.DrawRoisCount);
            // 命令仍留在撤销栈，用户可重试。
            Assert.True(host.ViewModel.UndoRedo.CanUndo);
            Assert.False(host.ViewModel.UndoRedo.CanRedo);
        }

        [Fact]
        public void Redo_WhenCommandThrows_LogsAndShowsErrorHintWithoutClaimingSuccess()
        {
            var host = new ControllerHost();
            host.ViewModel.UndoRedo.Execute(new FailSecondExecuteCommand());
            host.ViewModel.UndoRedo.Undo();

            RoiEditController controller = host.CreateController();

            controller.Redo();

            Assert.Single(host.Logs);
            Assert.Contains(host.StatusHints, hint => hint.Kind == StatusHintKind.Error);
            Assert.DoesNotContain(host.StatusHints, hint => hint.Message == "已重做");
            Assert.Equal(0, host.DrawRoisCount);
            Assert.True(host.ViewModel.UndoRedo.CanRedo);
        }

        [Fact]
        public void Undo_WhenCommandSucceeds_RefreshesAndShowsInfoHint()
        {
            var host = new ControllerHost();
            host.ViewModel.UndoRedo.Execute(new AddRoiCommand(new LineMeasureRoi(), host.ViewModel));

            RoiEditController controller = host.CreateController();

            controller.Undo();

            Assert.Empty(host.Logs);
            Assert.Contains(host.StatusHints, hint => hint.Message == "已撤销" && hint.Kind == StatusHintKind.Info);
            Assert.Equal(1, host.DrawRoisCount);
        }

        private sealed class ControllerHost
        {
            public ImageViewerViewModel ViewModel { get; } = new(RoiPluginRegistry.CreateBuiltIn());

            public List<string> Logs { get; } = [];

            public List<(string Message, StatusHintKind Kind)> StatusHints { get; } = [];

            public int DrawRoisCount { get; private set; }

            public RoiEditController CreateController()
            {
                return new RoiEditController(
                    ViewModel,
                    CreateDialogService(),
                    _ => { },
                    () => DrawRoisCount++,
                    (context, ex) => Logs.Add(context),
                    (message, kind) => StatusHints.Add((message, kind)));
            }

            private static ImageViewerDialogWorkflowService CreateDialogService()
            {
                var dependencies = new ImageViewerDialogWorkflowDependencies
                {
                    ImageLoading = new ImageViewerDialogImageLoadWorkflow
                    {
                        GetRetryCount = () => 0,
                        GetRetryDelayMilliseconds = () => 0,
                        PrepareReplacementAsync = (_, _) => Task.FromResult<Action?>(null),
                        SetImageLoadState = (_, _, _, _) => { },
                        ShowNonCriticalError = (_, _, _) => { }
                    },
                    RoiEditing = new ImageViewerDialogRoiWorkflow
                    {
                        GetPluginRegistry = () => RoiPluginRegistry.CreateBuiltIn(),
                        DrawRois = () => { },
                        DrawSelectedRoiLayer = () => { },
                        HandleRoiEdited = _ => { },
                        CreateStateCommand = (_, _, _) => null,
                        ExecuteUndoRedoCommand = _ => { },
                        ApplyCaliperDetection = _ => { }
                    },
                    Calibration = new ImageViewerDialogCalibrationWorkflow
                    {
                        GetPhysicalUnit = () => "px",
                        GetCalibration = () => null,
                        ApplyCalibration = (_, _, _) => { }
                    }
                };

                return new ImageViewerDialogWorkflowService(dependencies, new NullDialogAdapter());
            }
        }

        private sealed class NullDialogAdapter : IImageViewerDialogWorkflowAdapter
        {
            public string? ShowOpenImageDialog() => null;

            public string? ShowTextInput(string message, string defaultValue) => null;

            public string? ShowSaveRoiDialog() => null;

            public string? ShowOpenRoiDialog() => null;

            public string? ShowSaveSessionDialog() => null;

            public string? ShowOpenSessionDialog() => null;

            public string? ShowSaveProjectPackageDialog() => null;

            public string? ShowSaveRecipeDialog() => null;

            public string? ShowOpenRecipeDialog() => null;

            public string? ShowSaveSnapshotDialog() => null;

            public string? ShowSaveAnalysisCsvDialog() => null;

            public CalibrationDialogResult? ShowCalibrationDialog(string currentUnit, CameraCalibration? currentCalibration) => null;

            public CaliperMeasureRoi? ShowLineMeasureCaliperSettingsDialog(CaliperMeasureRoi roi, Action<CaliperMeasureRoi>? previewAction = null) => null;

            public LineCaliperMeasureRoi? ShowLineCaliperSettingsDialog(LineCaliperMeasureRoi roi, Action<LineCaliperMeasureRoi>? previewAction = null) => null;

            public CircularCaliperMeasureRoi? ShowCircularCaliperSettingsDialog(CircularCaliperMeasureRoi roi, Action<CircularCaliperMeasureRoi>? previewAction = null) => null;

            public void ShowPropertyEditor(string title, FrameworkElement editor)
            {
            }

            public void ShowReadOnlyText(string title, string text)
            {
            }

            public void ShowWarning(string title, string message)
            {
            }
        }

        private sealed class ThrowingOnUndoCommand : IUndoRedoCommand
        {
            public void Execute()
            {
            }

            public void Undo() => throw new InvalidOperationException("模拟撤销失败。");
        }

        private sealed class FailSecondExecuteCommand : IUndoRedoCommand
        {
            private int _count;

            public void Execute()
            {
                _count++;
                if (_count == 2)
                {
                    throw new InvalidOperationException("模拟重做失败。");
                }
            }

            public void Undo()
            {
            }
        }
    }
}
