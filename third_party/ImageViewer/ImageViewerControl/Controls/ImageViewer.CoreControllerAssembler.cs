using System;
using System.Windows;
using ImageViewer.Models;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        internal sealed class CoreControllerAssembler
        {
            private readonly ImageViewer _owner;
            private readonly ImageViewerDependencies _dependencies;

            public CoreControllerAssembler(ImageViewer owner, ImageViewerDependencies dependencies)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
                _dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
            }

            public ImageViewStateController CreateImageViewStateController()
            {
                return new ImageViewStateController(
                    new ImageViewerImageViewStateHost(
                        _owner,
                        _owner.imageContainer,
                        _owner.overlayCanvas,
                        _owner.snapGridCanvas,
                        _owner.pixelGridCanvas,
                        _owner.scaleBarCanvas,
                        _owner.scaleBarLine,
                        _owner.scaleBarText,
                        _owner.crosshairH,
                        _owner.crosshairV,
                        _owner.infoPanel,
                        _owner.roiListPanel,
                        _owner.scaleTransform,
                        _owner.UpdateRenderedImage,
                        _owner.UpdateCrosshair,
                        () => _owner.DrawRois(),
                        _owner.UpdatePixelGrid,
                        () => _owner.RequestViewportOverlayRefresh(),
                        _owner.FormatLength),
                    MinScale);
            }

            public RoiSelectionStateController CreateRoiSelectionStateController()
            {
                return new RoiSelectionStateController(
                    new ImageViewerRoiSelectionStateHost(
                        _owner,
                        _owner.roiPropertyEditorHost,
                        _owner.roiPropertyPanel,
                        roi => _owner.TryApplyCaliperDetection(roi),
                        () => _owner.DrawRois()));
            }

            public ViewModelController CreateViewModelController(RoiSelectionStateController roiSelectionStateController)
            {
                ArgumentNullException.ThrowIfNull(roiSelectionStateController);

                return new ViewModelController(
                    _owner.ViewModel,
                    _owner.UpdateContextMenuState,
                    roiSelectionStateController.HandleSelectedRoiChanged,
                    () => _owner.DrawRois(),
                    _owner.MarkDocumentDirty);
            }

            public ViewportController CreateViewportController()
            {
                return new ViewportController(
                    new ImageViewerViewportHost(
                        _owner,
                        () => new Size(_owner.rootGrid.ActualWidth, _owner.rootGrid.ActualHeight),
                        () => new ImageViewerViewportState(_owner.Scale, _owner.translateTransform.X, _owner.translateTransform.Y),
                        state =>
                        {
                            _owner.Scale = state.Scale;
                            _owner.translateTransform.X = state.TranslateX;
                            _owner.translateTransform.Y = state.TranslateY;
                        },
                        () => _owner.BeginViewportOverlayBatch(),
                        immediate => _owner.EndViewportOverlayBatch(immediate),
                        _owner.UpdatePixelGrid,
                        () => _owner.UpdateProfile(),
                        () => _owner.UpdateInfoPanel(),
                        _owner.UpdateRenderedImage,
                        () => _owner.RequestViewportOverlayRefresh()),
                    ViewportPadding,
                    MinScale,
                    MaxScale);
            }

            public ImageViewerDialogWorkflowService CreateDialogWorkflowService(
                RoiSelectionStateController roiSelectionStateController,
                ViewportController viewportController)
            {
                ArgumentNullException.ThrowIfNull(roiSelectionStateController);
                ArgumentNullException.ThrowIfNull(viewportController);

                IImageViewerDialogWorkflowAdapter dialogWorkflowAdapter = _dependencies.CreateDialogWorkflowAdapter(_owner.GetDocumentDialogWindow);
                return _dependencies.CreateDialogWorkflowService(
                    new ImageViewerDialogWorkflowDependencies
                    {
                        ImageLoading = new ImageViewerDialogImageLoadWorkflow
                        {
                            GetRetryCount = () => _owner.RuntimeOptions.ImageLoadRetryCount,
                            GetRetryDelayMilliseconds = () => _owner.RuntimeOptions.ImageLoadRetryDelayMilliseconds,
                            PrepareReplacementAsync = _owner.PrepareOpenedImageAsync,
                            SuspendReplacementWrites = () => _owner.SuspendImageReplacementWrites?.Invoke(),
                            SetImageLoadState = _owner.SetImageLoadState,
                            ShowNonCriticalError = _owner.ShowNonCriticalError
                        },
                        RoiEditing = new ImageViewerDialogRoiWorkflow
                        {
                            GetPluginRegistry = () => _owner.PluginRegistry,
                            DrawRois = () => _owner.DrawRois(),
                            DrawSelectedRoiLayer = _owner.DrawSelectedRoiLayer,
                            HandleRoiEdited = roiSelectionStateController.HandleRoiEdited,
                            CreateStateCommand = static (roi, oldState, newState) => ImageViewer.CreateStateCommand(roi, oldState, newState),
                            ExecuteUndoRedoCommand = command => _owner.ViewerState.UndoRedo.Execute(command),
                            ApplyCaliperDetection = roi => _owner.TryApplyCaliperDetection(roi)
                        },
                        Calibration = new ImageViewerDialogCalibrationWorkflow
                        {
                            GetPhysicalUnit = () => _owner.PhysicalUnit,
                            GetCalibration = () => _owner.Calibration,
                            NormalizeCalibration = distortion => NormalizeCalibration(distortion, _owner.ImageSource?.Width ?? 0, _owner.ImageSource?.Height ?? 0),
                            ShowCalibrationError = message => _owner.ShowStatusHint(message, StatusHintKind.Error),
                            ApplyCalibration = (pixelSize, unit, distortion) =>
                            {
                                _owner.PixelSize = pixelSize;
                                _owner.PhysicalUnit = unit;
                                _owner.Calibration = distortion;
                            }
                        }
                    },
                    dialogWorkflowAdapter);
            }

            /// <summary>
            /// 归一化对话框返回的畸变模型：
            /// Chinese: 主点或归一化半径缺失（&lt;= 0）时按图像中心 / 短边一半补齐，与 CameraCalibration.CreateForImage
            /// 的推断一致；这样纯"已知长度 → PixelSize"标定也能带上合理的几何基准。
            /// </summary>
            private static CameraCalibration? NormalizeCalibration(CameraCalibration? calibration, double imageWidth, double imageHeight)
            {
                if (calibration is null)
                {
                    return null;
                }

                bool hasGeometry = calibration.NormalizationRadius != 1 ||
                    calibration.PrincipalX != 0 || calibration.PrincipalY != 0;
                if (hasGeometry || imageWidth <= 0 || imageHeight <= 0)
                {
                    return calibration;
                }

                return new CameraCalibration
                {
                    K1 = calibration.K1,
                    K2 = calibration.K2,
                    K3 = calibration.K3,
                    TangentialP1 = calibration.TangentialP1,
                    TangentialP2 = calibration.TangentialP2,
                    PrincipalX = imageWidth / 2.0,
                    PrincipalY = imageHeight / 2.0,
                    NormalizationRadius = Math.Min(imageWidth, imageHeight) / 2.0,
                    ReprojectionErrorRms = calibration.ReprojectionErrorRms,
                    MeasurementUncertaintyPixels = calibration.MeasurementUncertaintyPixels,
                    CalibrationId = calibration.CalibrationId,
                    CalibratedAtUtc = calibration.CalibratedAtUtc
                };
            }

            public ImageSourceController CreateImageSourceController(
                IImageViewStateController imageViewStateController)
            {
                ArgumentNullException.ThrowIfNull(imageViewStateController);

                return new ImageSourceController(
                    () => _owner.IsLoaded,
                    () => _owner.ImageSource,
                    source => _owner.PrepareAnalysisResourcesAsync(source),
                    _owner.BackgroundOperationObserver.ObserveAsync,
                    imageViewStateController,
                    _owner.RefreshAllCaliperDetections,
                    () => _owner.DrawRois());
            }

            public RoiEditController CreateRoiEditController(ImageViewerDialogWorkflowService dialogWorkflowService)
            {
                ArgumentNullException.ThrowIfNull(dialogWorkflowService);

                return new RoiEditController(
                    _owner.ViewModel,
                    dialogWorkflowService,
                    roi => _owner.TryRefreshCaliperDetection(roi),
                    () => _owner.DrawRois(),
                    _owner.LogNonCriticalError,
                    (message, kind) => _owner.ShowStatusHint(message, kind));
            }

            public CalibrationController CreateCalibrationController(ImageViewerDialogWorkflowService dialogWorkflowService)
            {
                ArgumentNullException.ThrowIfNull(dialogWorkflowService);
                return new CalibrationController(dialogWorkflowService, () => _owner.ViewerState.SelectedRoi);
            }

            public DroppedContentController CreateDroppedContentController(
                ImageViewerSessionController sessionController)
            {
                ArgumentNullException.ThrowIfNull(sessionController);

                return new DroppedContentController(
                    path => _owner.FileImageWorkflow.OpenImageFromPathAsync(path),
                    sessionController,
                    () => _owner._roiSelectionStateController.RefreshPropertyPanel(),
                    (message, kind) => _owner.ShowStatusHint(message, kind));
            }

            public ExternalImageSourceBindingController CreateExternalImageSourceBindingController()
            {
                return new ExternalImageSourceBindingController(
                    _owner,
                    () => _owner.ImageSource,
                    source => _owner.SetCurrentValue(ImageSourceProperty, source));
            }
        }
    }
}
