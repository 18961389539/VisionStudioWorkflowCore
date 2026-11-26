using System;
using System.Threading.Tasks;
using ImageViewer.Controls;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.ViewModels;
using Xunit;

namespace ImageViewerControl.Tests
{
    public class ImageViewerDialogWorkflowHostAdapterTests
    {
        [Fact]
        public void ShowCaliperSettings_UsesInjectedStateCommandAndUndoExecutor()
        {
            RoiBase? capturedRoi = null;
            RoiBase? capturedOldState = null;
            RoiBase? capturedNewState = null;
            var expectedCommand = new TestUndoRedoCommand();
            var state = new WorkflowState
            {
                CreateStateCommand = (roi, oldState, newState) =>
                {
                    capturedRoi = roi;
                    capturedOldState = oldState;
                    capturedNewState = newState;
                    return expectedCommand;
                }
            };
            var adapter = new FakeDialogWorkflowAdapter
            {
                LineMeasureCaliperResult = new CaliperMeasureRoi()
            };
            var service = new ImageViewerDialogWorkflowService(CreateDependencies(state), adapter);
            var roi = new CaliperMeasureRoi();

            service.ShowCaliperSettings(roi);

            Assert.Same(roi, capturedRoi);
            Assert.NotNull(capturedOldState);
            Assert.NotNull(capturedNewState);
            Assert.Same(expectedCommand, Assert.Single(state.ExecutedCommands));
        }

        [Fact]
        public void ShowCaliperSettings_UsesInjectedDetectionDelegates()
        {
            var state = new WorkflowState();
            var adapter = new FakeDialogWorkflowAdapter
            {
                LineMeasureCaliperResult = new CaliperMeasureRoi(),
                LineCaliperResult = new LineCaliperMeasureRoi(),
                CircularCaliperResult = new CircularCaliperMeasureRoi()
            };
            var service = new ImageViewerDialogWorkflowService(CreateDependencies(state), adapter);

            service.ShowCaliperSettings(new CaliperMeasureRoi());
            service.ShowCaliperSettings(new LineCaliperMeasureRoi());
            service.ShowCaliperSettings(new CircularCaliperMeasureRoi());

            Assert.Equal(1, state.CaliperDetectionCount);
            Assert.Equal(1, state.LineCaliperDetectionCount);
            Assert.Equal(1, state.CircularCaliperDetectionCount);
        }

        private static ImageViewerDialogWorkflowDependencies CreateDependencies(WorkflowState state)
        {
            return new ImageViewerDialogWorkflowDependencies
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
                    DrawRois = () => state.DrawRoiCount++,
                    DrawSelectedRoiLayer = () => state.DrawSelectedRoiLayerCount++,
                    HandleRoiEdited = _ => { },
                    CreateStateCommand = state.CreateStateCommand,
                    ExecuteUndoRedoCommand = command => state.ExecutedCommands.Add(command),
                    ApplyCaliperDetection = roi =>
                    {
                        switch (roi)
                        {
                            case CaliperMeasureRoi:
                                state.CaliperDetectionCount++;
                                break;
                            case LineCaliperMeasureRoi:
                                state.LineCaliperDetectionCount++;
                                break;
                            case CircularCaliperMeasureRoi:
                                state.CircularCaliperDetectionCount++;
                                break;
                        }
                    }
                },
                Calibration = new ImageViewerDialogCalibrationWorkflow
                {
                    GetPhysicalUnit = () => "px",
                    GetCalibration = () => null,
                    ApplyCalibration = (_, _, _) => { }
                }
            };
        }

        private sealed class WorkflowState
        {
            public int DrawRoiCount { get; set; }

            public int DrawSelectedRoiLayerCount { get; set; }

            public int CaliperDetectionCount { get; set; }

            public int LineCaliperDetectionCount { get; set; }

            public int CircularCaliperDetectionCount { get; set; }

            public List<IUndoRedoCommand> ExecutedCommands { get; } = [];

            public Func<RoiBase, RoiBase, RoiBase, IUndoRedoCommand?> CreateStateCommand { get; set; } = (_, _, _) => null;
        }

        private sealed class FakeDialogWorkflowAdapter : IImageViewerDialogWorkflowAdapter
        {
            public CaliperMeasureRoi? LineMeasureCaliperResult { get; set; }

            public LineCaliperMeasureRoi? LineCaliperResult { get; set; }

            public CircularCaliperMeasureRoi? CircularCaliperResult { get; set; }

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

            public CaliperMeasureRoi? ShowLineMeasureCaliperSettingsDialog(CaliperMeasureRoi roi, Action<CaliperMeasureRoi>? previewAction = null) => LineMeasureCaliperResult;

            public LineCaliperMeasureRoi? ShowLineCaliperSettingsDialog(LineCaliperMeasureRoi roi, Action<LineCaliperMeasureRoi>? previewAction = null) => LineCaliperResult;

            public CircularCaliperMeasureRoi? ShowCircularCaliperSettingsDialog(CircularCaliperMeasureRoi roi, Action<CircularCaliperMeasureRoi>? previewAction = null) => CircularCaliperResult;

            public void ShowPropertyEditor(string title, System.Windows.FrameworkElement editor) { }

            public void ShowReadOnlyText(string title, string text) { }

            public void ShowWarning(string title, string message) { }
        }

        private sealed class TestUndoRedoCommand : IUndoRedoCommand
        {
            public int ExecuteCount { get; private set; }
            public int UndoCount { get; private set; }

            public void Execute() => ExecuteCount++;

            public void Undo() => UndoCount++;
        }
    }
}
