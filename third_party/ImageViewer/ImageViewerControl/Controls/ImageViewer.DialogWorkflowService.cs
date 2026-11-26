using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Abstractions;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Services;
using ImageViewer.Utils;
using ImageViewer.ViewModels;

namespace ImageViewer.Controls
{
    internal sealed class ImageViewerDialogWorkflowDependencies
    {
        public required ImageViewerDialogImageLoadWorkflow ImageLoading { get; init; }

        public required ImageViewerDialogRoiWorkflow RoiEditing { get; init; }

        public required ImageViewerDialogCalibrationWorkflow Calibration { get; init; }
    }

    internal sealed class ImageViewerDialogImageLoadWorkflow
    {
        public required Func<int> GetRetryCount { get; init; }

        public required Func<int> GetRetryDelayMilliseconds { get; init; }

        public required Func<ImageSource, CancellationToken, Task<Action?>> PrepareReplacementAsync { get; init; }

        public Func<IDisposable?>? SuspendReplacementWrites { get; init; }

        public required Action<bool, string, double, bool> SetImageLoadState { get; init; }

        public required Action<string, string, Exception> ShowNonCriticalError { get; init; }
    }

    internal sealed class ImageViewerDialogRoiWorkflow
    {
        public required Func<RoiPluginRegistry> GetPluginRegistry { get; init; }

        public required Action DrawRois { get; init; }

        public required Action DrawSelectedRoiLayer { get; init; }

        public required Action<RoiBase> HandleRoiEdited { get; init; }

        public required Func<RoiBase, RoiBase, RoiBase, IUndoRedoCommand?> CreateStateCommand { get; init; }

        public required Action<IUndoRedoCommand> ExecuteUndoRedoCommand { get; init; }

        public required Action<RoiBase> ApplyCaliperDetection { get; init; }
    }

    internal sealed class ImageViewerDialogCalibrationWorkflow
    {
        public required Func<string> GetPhysicalUnit { get; init; }

        /// <summary>当前已生效的畸变标定，用于预填对话框。</summary>
        public required Func<CameraCalibration?> GetCalibration { get; init; }

        public Func<CameraCalibration?, CameraCalibration?>? NormalizeCalibration { get; init; }

        public Action<string>? ShowCalibrationError { get; init; }

        /// <summary>
        /// 应用比例与同一套已归一化的畸变模型（可能为 null 表示不校正）。
        /// </summary>
        public required Action<double, string, CameraCalibration?> ApplyCalibration { get; init; }
    }

    internal sealed class ImageViewerDialogWorkflowService
    {
        private readonly ImageViewerDialogWorkflowDependencies _dependencies;
        private readonly IImageViewerDialogWorkflowAdapter _adapter;
        private readonly object _imageLoadGate = new();
        private CancellationTokenSource? _activeImageLoadCancellation;
        private long _imageLoadGeneration;
        private string? _lastFailedImagePath;

        public ImageViewerDialogWorkflowService(ImageViewerDialogWorkflowDependencies dependencies, IImageViewerDialogWorkflowAdapter adapter)
        {
            _dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        }

        public async Task OpenImageAsync()
        {
            string? filePath = _adapter.ShowOpenImageDialog();
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            await OpenImageFromPathAsync(filePath);
        }

        public async Task RetryLastImageLoadAsync()
        {
            if (string.IsNullOrWhiteSpace(_lastFailedImagePath))
            {
                return;
            }

            await OpenImageFromPathAsync(_lastFailedImagePath);
        }

        public void CancelImageLoad()
        {
            CancellationTokenSource? cancellation;
            lock (_imageLoadGate)
            {
                cancellation = _activeImageLoadCancellation;
                if (cancellation == null)
                {
                    return;
                }

                _activeImageLoadCancellation = null;
                _imageLoadGeneration++;
            }

            cancellation.Cancel();
            _dependencies.ImageLoading.SetImageLoadState(
                false,
                UiText.Get("ImageLoadStatusCanceled"),
                0,
                false);
        }

        public async Task OpenImageFromPathAsync(string filePath)
        {
            (CancellationTokenSource cancellation, long generation) = BeginImageLoad();
            ImageViewerDialogImageLoadWorkflow imageLoading = _dependencies.ImageLoading;
            try
            {
                _lastFailedImagePath = null;
                int retryCount = Math.Max(0, imageLoading.GetRetryCount());
                int retryDelayMilliseconds = Math.Max(0, imageLoading.GetRetryDelayMilliseconds());
                Exception? lastException = null;

                if (!IsCurrentImageLoad(cancellation, generation))
                {
                    return;
                }

                imageLoading.SetImageLoadState(true, UiText.Get("ImageLoadStatusStarting"), 5, false);

                for (int attempt = 0; attempt <= retryCount; attempt++)
                {
                    try
                    {
                        if (!IsCurrentImageLoad(cancellation, generation))
                        {
                            return;
                        }

                        if (attempt > 0)
                        {
                            imageLoading.SetImageLoadState(true, UiText.Get("ImageLoadStatusRetrying"), 15 + attempt * 10, false);
                            if (retryDelayMilliseconds > 0)
                            {
                                await Task.Delay(retryDelayMilliseconds, cancellation.Token);
                            }
                        }

                        imageLoading.SetImageLoadState(true, UiText.Get("ImageLoadStatusDecoding"), 10, false);
                        var decodeProgress = new Progress<double>(progress =>
                        {
                            if (IsCurrentImageLoad(cancellation, generation))
                            {
                                imageLoading.SetImageLoadState(true, UiText.Get("ImageLoadStatusDecoding"), Math.Min(progress, 80), false);
                            }
                        });
                        BitmapSource bitmap = await ImageViewerImageDecoder.LoadDocumentImageAsync(
                            filePath, decodeProgress, expectedSha256: null, cancellation.Token);
                        if (!IsCurrentImageLoad(cancellation, generation))
                        {
                            return;
                        }

                        using var writeSuspension = imageLoading.SuspendReplacementWrites?.Invoke();
                        Action? replace = await imageLoading.PrepareReplacementAsync(bitmap, cancellation.Token);
                        if (!IsCurrentImageLoad(cancellation, generation))
                        {
                            return;
                        }
                        if (replace == null)
                        {
                            imageLoading.SetImageLoadState(false, UiText.Get("ImageLoadStatusCanceled"), 0, false);
                            return;
                        }

                        imageLoading.SetImageLoadState(true, UiText.Get("ImageLoadStatusApplying"), 85, false);
                        replace();
                        if (IsCurrentImageLoad(cancellation, generation))
                        {
                            imageLoading.SetImageLoadState(false, UiText.Get("ImageLoadStatusReady"), 100, false);
                            _lastFailedImagePath = null;
                        }
                        return;
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (!IsCurrentImageLoad(cancellation, generation))
                        {
                            return;
                        }

                        lastException = ex;
                        if (ex is ImageViewerImageTooLargeException || attempt >= retryCount)
                        {
                            break;
                        }
                    }
                }

                if (!IsCurrentImageLoad(cancellation, generation))
                {
                    return;
                }

                _lastFailedImagePath = filePath;
                imageLoading.SetImageLoadState(true, UiText.Get("ImageLoadStatusFailed"), 0, true);
                if (lastException is ImageViewerImageTooLargeException tooLarge)
                {
                    imageLoading.ShowNonCriticalError(
                        UiText.Get("ErrorImageTooLargeTitle"),
                        UiText.Format("ErrorImageTooLargeMessage", tooLarge.PixelWidth, tooLarge.PixelHeight, tooLarge.MaxPixelCount),
                        tooLarge);
                }
                else
                {
                    imageLoading.ShowNonCriticalError(UiText.Get("ErrorOpenImageTitle"), UiText.Get("ErrorOpenImageMessage"), lastException ?? new InvalidOperationException("Image load failed."));
                }
            }
            finally
            {
                EndImageLoad(cancellation);
            }
        }

        public async Task<bool> RunDocumentLoadAsync(Func<CancellationToken, IProgress<double>, Task<Action?>> prepare)
        {
            (CancellationTokenSource cancellation, long generation) = BeginImageLoad();
            ImageViewerDialogImageLoadWorkflow imageLoading = _dependencies.ImageLoading;
            _lastFailedImagePath = null;
            try
            {
                imageLoading.SetImageLoadState(true, UiText.Get("DocumentLoadStatusStarting"), 5, false);
                var progress = new Progress<double>(value =>
                {
                    if (IsCurrentImageLoad(cancellation, generation))
                    {
                        imageLoading.SetImageLoadState(true, UiText.Get("ImageLoadStatusDecoding"), Math.Min(value, 80), false);
                    }
                });
                Action? apply = await prepare(cancellation.Token, progress);
                if (!IsCurrentImageLoad(cancellation, generation))
                {
                    return false;
                }

                if (apply == null)
                {
                    imageLoading.SetImageLoadState(false, string.Empty, 0, false);
                    return false;
                }

                imageLoading.SetImageLoadState(true, UiText.Get("DocumentLoadStatusApplying"), 85, false);
                apply();
                if (IsCurrentImageLoad(cancellation, generation))
                {
                    imageLoading.SetImageLoadState(false, UiText.Get("DocumentLoadStatusReady"), 100, false);
                }

                return true;
            }
            catch (Exception) when (!IsCurrentImageLoad(cancellation, generation))
            {
                return false;
            }
            catch
            {
                imageLoading.SetImageLoadState(false, UiText.Get("DocumentLoadStatusFailed"), 0, false);
                throw;
            }
            finally
            {
                EndImageLoad(cancellation);
            }
        }

        private (CancellationTokenSource Cancellation, long Generation) BeginImageLoad()
        {
            lock (_imageLoadGate)
            {
                _activeImageLoadCancellation?.Cancel();
                var cancellation = new CancellationTokenSource();
                _activeImageLoadCancellation = cancellation;
                _imageLoadGeneration++;
                return (cancellation, _imageLoadGeneration);
            }
        }

        private bool IsCurrentImageLoad(CancellationTokenSource cancellation, long generation)
        {
            lock (_imageLoadGate)
            {
                return ReferenceEquals(_activeImageLoadCancellation, cancellation)
                    && _imageLoadGeneration == generation
                    && !cancellation.IsCancellationRequested;
            }
        }

        private void EndImageLoad(CancellationTokenSource cancellation)
        {
            lock (_imageLoadGate)
            {
                if (ReferenceEquals(_activeImageLoadCancellation, cancellation))
                {
                    _activeImageLoadCancellation = null;
                }
            }

            cancellation.Dispose();
        }

        public string? ShowTextInput(string message, string defaultValue)
        {
            return _adapter.ShowTextInput(message, defaultValue);
        }

        public string? ShowSaveRoiDialog()
        {
            return _adapter.ShowSaveRoiDialog();
        }

        public string? ShowOpenRoiDialog()
        {
            return _adapter.ShowOpenRoiDialog();
        }

        public string? ShowSaveSessionDialog()
        {
            return _adapter.ShowSaveSessionDialog();
        }

        public string? ShowOpenSessionDialog()
        {
            return _adapter.ShowOpenSessionDialog();
        }

        public string? ShowSaveProjectPackageDialog()
        {
            return _adapter.ShowSaveProjectPackageDialog();
        }

        public string? ShowSaveRecipeDialog()
        {
            return _adapter.ShowSaveRecipeDialog();
        }

        public string? ShowOpenRecipeDialog()
        {
            return _adapter.ShowOpenRecipeDialog();
        }

        public string? ShowSaveSnapshotDialog()
        {
            return _adapter.ShowSaveSnapshotDialog();
        }

        public string? ShowSaveAnalysisCsvDialog()
        {
            return _adapter.ShowSaveAnalysisCsvDialog();
        }

        public void ShowReadOnlyText(string title, string text)
        {
            _adapter.ShowReadOnlyText(title, text);
        }

        public void ShowWarning(string title, string message)
        {
            _adapter.ShowWarning(title, message);
        }

        public void ShowRoiProperties(RoiBase roi)
        {
            ArgumentNullException.ThrowIfNull(roi);
            ImageViewerDialogRoiWorkflow roiEditing = _dependencies.RoiEditing;

            if (roi is CaliperMeasureRoi or LineCaliperMeasureRoi or CircularCaliperMeasureRoi)
            {
                ShowCaliperSettings(roi);
                return;
            }

            FrameworkElement? editor = roiEditing.GetPluginRegistry().FindByRoi(roi)?.CreatePropertyEditor(roi);
            if (editor == null)
            {
                return;
            }

            _adapter.ShowPropertyEditor(UiText.Format("DialogEditRoiTitle", roi.DisplayTypeName), editor);
            roiEditing.HandleRoiEdited(roi);
        }

        public void ShowCaliperSettings(RoiBase roi)
        {
            switch (roi)
            {
                case LineCaliperMeasureRoi lineCaliper:
                    ShowCaliperSettingsDialogCore(lineCaliper, _adapter.ShowLineCaliperSettingsDialog, CreateConfiguredLineCaliperState);
                    return;
                case CircularCaliperMeasureRoi circularCaliper:
                    ShowCaliperSettingsDialogCore(circularCaliper, _adapter.ShowCircularCaliperSettingsDialog, CreateConfiguredCircularCaliperState);
                    return;
                case CaliperMeasureRoi line:
                    ShowCaliperSettingsDialogCore(line, _adapter.ShowLineMeasureCaliperSettingsDialog, CreateConfiguredCaliperState);
                    return;
            }
        }

        public string? ShowRoiLabelDialog(RoiBase roi)
        {
            ArgumentNullException.ThrowIfNull(roi);
            return ShowTextInput(UiText.Get("DialogRoiLabelPrompt"), roi.Label);
        }

        public void CalibrateSelectedRoi(RoiBase? selectedRoi)
        {
            Point p1;
            Point p2;

            switch (selectedRoi)
            {
                case CaliperMeasureRoi caliper:
                    p1 = caliper.P1.ToWpfPoint();
                    p2 = caliper.P2.ToWpfPoint();
                    break;
                case LineMeasureRoi line:
                    p1 = line.P1.ToWpfPoint();
                    p2 = line.P2.ToWpfPoint();
                    break;
                default:
                    return;
            }

            double pixelDistance = GeometryUtils.Distance(p1, p2);
            if (pixelDistance <= 0)
            {
                return;
            }

            var calibration = _adapter.ShowCalibrationDialog(
                _dependencies.Calibration.GetPhysicalUnit(),
                _dependencies.Calibration.GetCalibration());
            if (calibration is not { } result)
            {
                return;
            }

            var workflow = _dependencies.Calibration;
            CameraCalibration? model = workflow.NormalizeCalibration?.Invoke(result.Distortion) ?? result.Distortion;
            var reference = new LineMeasureRoi { P1 = p1.ToPointD(), P2 = p2.ToPointD() };
            // Use exactly the same audited pixel length as later measurements of this reference.
            double? correctedPixels = RoiInspectionEvaluator.Evaluate(reference, pixelSize: 1, model).MeasuredValue;
            double pixelSize = correctedPixels is double pixels && pixels > 0 ? result.Length / pixels : double.NaN;
            if (!double.IsFinite(pixelSize) || pixelSize <= 0)
            {
                workflow.ShowCalibrationError?.Invoke(UiText.Get("CalibrationReferenceCorrectionFailed"));
                return;
            }
            workflow.ApplyCalibration(pixelSize, result.Unit, model);
        }

        private void ShowCaliperSettingsDialogCore<TCaliper>(
            TCaliper roi,
            Func<TCaliper, Action<TCaliper>?, TCaliper?> showDialog,
            Func<TCaliper, TCaliper, TCaliper> createConfiguredState)
            where TCaliper : RoiBase
        {
            ImageViewerDialogRoiWorkflow roiEditing = _dependencies.RoiEditing;
            var originalState = (TCaliper)roi.Clone();
            TCaliper? configuredState = showDialog(
                roi,
                preview => PreviewConfiguredCaliper(roi, preview, createConfiguredState));
            if (configuredState == null)
            {
                roi.ApplyFrom(originalState);
                roiEditing.DrawRois();
                return;
            }

            RoiBase newState = createConfiguredState(originalState, configuredState);
            roi.ApplyFrom(originalState);

            IUndoRedoCommand? command = roiEditing.CreateStateCommand(roi, originalState, newState);
            if (command != null)
            {
                roiEditing.ExecuteUndoRedoCommand(command);
            }

            roiEditing.DrawRois();
        }

        private void PreviewConfiguredCaliper<TCaliper>(
            TCaliper targetCaliper,
            TCaliper configuredCaliper,
            Func<TCaliper, TCaliper, TCaliper> createConfiguredState)
            where TCaliper : RoiBase
        {
            TCaliper previewState = createConfiguredState(targetCaliper, configuredCaliper);
            targetCaliper.ApplyFrom(previewState);
            _dependencies.RoiEditing.DrawSelectedRoiLayer();
        }

        private CaliperMeasureRoi CreateConfiguredCaliperState(CaliperMeasureRoi geometrySource, CaliperMeasureRoi configuredLine)
        {
            return CreateConfiguredCaliperState(geometrySource, configuredLine, CopyLineGeometry);
        }

        private CircularCaliperMeasureRoi CreateConfiguredCircularCaliperState(CircularCaliperMeasureRoi geometrySource, CircularCaliperMeasureRoi configuredCaliper)
        {
            return CreateConfiguredCaliperState(geometrySource, configuredCaliper, CopyCircleGeometry);
        }

        private LineCaliperMeasureRoi CreateConfiguredLineCaliperState(LineCaliperMeasureRoi geometrySource, LineCaliperMeasureRoi configuredLine)
        {
            return CreateConfiguredCaliperState(geometrySource, configuredLine, CopyLineGeometry);
        }

        private TCaliper CreateConfiguredCaliperState<TCaliper>(
            TCaliper geometrySource,
            TCaliper configuredCaliper,
            Action<TCaliper, TCaliper> copyGeometry)
            where TCaliper : RoiBase
        {
            var state = (TCaliper)configuredCaliper.Clone();
            copyGeometry(geometrySource, state);
            CopyCommonRoiState(geometrySource, state);
            CaliperRoiDetection.Clear(state);
            _dependencies.RoiEditing.ApplyCaliperDetection(state);
            return state;
        }

        private static void CopyCommonRoiState(RoiBase source, RoiBase target)
        {
            target.Label = source.Label;
            target.CopyVisualStateFrom(source);
        }

        private static void CopyLineGeometry(LineMeasureRoi source, LineMeasureRoi target)
        {
            target.P1 = source.P1;
            target.P2 = source.P2;
        }

        private static void CopyCircleGeometry(CircleRoi source, CircleRoi target)
        {
            target.Center = source.Center;
            target.Radius = source.Radius;
        }

    }
}
