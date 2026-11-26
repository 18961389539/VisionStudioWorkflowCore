using System;
using System.Windows;
using System.Windows.Media.Imaging;
using ImageViewer.Models;
using ImageViewer.Services;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        internal sealed class SessionCompositionAssembler
        {
            private readonly ImageViewer _owner;

            public SessionCompositionAssembler(ImageViewer owner)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            }

            public ImageViewerSessionComposition CreateSessionComposition(
                ImageViewerDialogWorkflowService dialogWorkflowService,
                ViewportController viewportController)
            {
                ArgumentNullException.ThrowIfNull(dialogWorkflowService);
                ArgumentNullException.ThrowIfNull(viewportController);

                var sessionController = new ImageViewerSessionController(
                    new ImageViewerSessionControllerDependencies
                    {
                        Persistence = new ImageViewerSessionPersistenceWorkflow
                        {
                            ShowSaveSessionDialog = dialogWorkflowService.ShowSaveSessionDialog,
                            ConfirmUnsavedChanges = () => _owner.DialogService.ConfirmUnsavedDocumentChanges(_owner.GetDocumentDialogWindow()),
                            ShowOpenSessionDialog = dialogWorkflowService.ShowOpenSessionDialog,
                            LocateSessionImage = (imagePath, sessionPath) =>
                                _owner.RuntimeServices.FileDialogService.ShowLocateSessionImageDialog(_owner.GetDocumentDialogWindow(), imagePath, sessionPath),
                            ShowSaveProjectPackageDialog = dialogWorkflowService.ShowSaveProjectPackageDialog,
                            ShowSaveRecipeDialog = dialogWorkflowService.ShowSaveRecipeDialog,
                            ShowOpenRecipeDialog = dialogWorkflowService.ShowOpenRecipeDialog,
                            SessionService = _owner.RuntimeServices.SessionService,
                            RecentProjectService = _owner.RuntimeServices.RecentProjectService,
                            ProjectPackageService = _owner.RuntimeServices.ProjectPackageService,
                            GetPluginRegistry = () => _owner.PluginRegistry,
                            GetAllRois = () => _owner.ViewerState.AllRois,
                            GetPixelSize = () => _owner.PixelSize,
                            SetPixelSize = value => _owner.SetCurrentValue(PixelSizeProperty, value),
                            GetPhysicalUnit = () => _owner.PhysicalUnit,
                            SetPhysicalUnit = value => _owner.SetCurrentValue(PhysicalUnitProperty, value),
                            GetCalibration = () => _owner.Calibration,
                            SetCalibration = value => _owner.SetCurrentValue(CalibrationProperty, value),
                            GetDisplay = () => _owner.DisplayAdjustment,
                            SetDisplay = value => _owner.SetCurrentValue(DisplayAdjustmentProperty, value),
                            GetWorkpieceCoordinateSystem = () => _owner.WorkpieceCoordinateSystem,
                            SetWorkpieceCoordinateSystem = value => _owner.WorkpieceCoordinateSystem = value,
                            GetRecipeDatumPoints = _owner.GetRecipeReferenceDatums,
                            GetCurrentRecipeDatumPoints = _owner.GetRecipeCurrentDatums,
                            GetCurrentImageIdentity = () => _owner.ImageSource,
                            GetSelectedRecipeDatumPosition = () => _owner.ViewerState.SelectedRoi is PointCoordinateMeasureRoi point
                                ? point.Position
                                : null,
                            SetRecipeDatumPoint = _owner.SetRecipeReferenceDatum,
                            SetRecipeDatumPoints = _owner.SetRecipeReferenceDatums,
                            SetCurrentRecipeDatumPoints = _owner.SetRecipeCurrentDatums,
                            SetCurrentRecipeDatumPoint = _owner.SetRecipeCurrentDatum,
                            GetQualityProfile = () => _owner.QualityProfile,
                            SetQualityProfile = value => _owner.QualityProfile = value,
                            GetCurrentViewportState = () => viewportController.CurrentState,
                            TryGetCurrentImagePath = viewportController.TryGetCurrentImagePath,
                            GetCurrentBitmap = () => _owner.ImageSource as BitmapSource,
                            RunDocumentLoadAsync = dialogWorkflowService.RunDocumentLoadAsync,
                            PrepareImageAsync = async (filePath, expectedSha256, token, progress) =>
                            {
                                var bitmap = await ImageViewerImageDecoder.LoadDocumentImageAsync(filePath, progress, expectedSha256, token);
                                return () => _owner.SetCurrentValue(ImageSourceProperty, bitmap);
                            },
                            ClearImage = () => _owner.SetCurrentValue(ImageSourceProperty, null),
                            EndDocumentInteraction = () =>
                            {
                                _owner.ExitCurrentMode();
                                _owner.ResetImageOrientation();
                                _owner.ClearAllRecipeDatums();
                            },
                            FitImageToView = viewportController.FitToView,
                            ReplaceAllRois = _owner.ViewerState.ReplaceAllRois,
                            ApplyViewportState = state => viewportController.ApplyViewportState(state),
                            DrawRois = () => _owner.DrawRois(),
                            ShowNonCriticalError = _owner.ShowNonCriticalError,
                            ShowWarning = (title, message) =>
                            {
                                _owner.ShowStatusHint(message, StatusHintKind.Error);
                                if (Window.GetWindow(_owner) != null)
                                {
                                    dialogWorkflowService.ShowWarning(title, message);
                                }
                            },
                            ShowStatusHint = (message, kind) => _owner.ShowStatusHint(message, kind),
                            ClearUndoHistory = () => _owner.ViewerState.UndoRedo.Clear(),
                            UpdateContextMenuState = _owner.UpdateContextMenuState
                        },
                        AutoSave = new ImageViewerAutoSaveWorkflow
                        {
                            Dispatcher = _owner.Dispatcher,
                            HasContent = () => _owner.ImageSource != null || _owner.ViewerState.AllRois.Count > 0,
                            LogNonCriticalError = _owner.LogNonCriticalError,
                            ShowStatusHint = (message, kind) => _owner.ShowStatusHint(message, kind)
                        }
                    },
                    _owner.HostServices.PeriodicTaskSchedulerFactory,
                    _owner.HostServices.SessionStoragePolicy);

                ImageViewerRoiPersistenceController roiPersistenceController = CreateRoiPersistenceController(dialogWorkflowService);
                ImageViewerFileMenuCommandController fileMenuCommandController = CreateFileMenuCommandController(sessionController, roiPersistenceController);

                return new ImageViewerSessionComposition(sessionController, roiPersistenceController, fileMenuCommandController);
            }

            private ImageViewerRoiPersistenceController CreateRoiPersistenceController(ImageViewerDialogWorkflowService dialogWorkflowService)
            {
                ArgumentNullException.ThrowIfNull(dialogWorkflowService);

                return new ImageViewerRoiPersistenceController(
                    new ImageViewerRoiPersistenceControllerHostAdapter(
                        _owner,
                        dialogWorkflowService,
                        _owner.RefreshAllCaliperDetections,
                        () => _owner.DrawRois(),
                        () => _owner._roiSelectionStateController.RefreshPropertyPanel(),
                        _owner.ShowNonCriticalError));
            }

            private ImageViewerFileMenuCommandController CreateFileMenuCommandController(
                ImageViewerSessionController sessionController,
                ImageViewerRoiPersistenceController roiPersistenceController)
            {
                ArgumentNullException.ThrowIfNull(sessionController);
                ArgumentNullException.ThrowIfNull(roiPersistenceController);

                return new ImageViewerFileMenuCommandController(
                    new ImageViewerFileMenuCommandHostAdapter(
                        new ImageViewerFileDialogCapability(_owner.ShowOpenImageDialogAsync),
                        new ImageViewerFileOperationsCapability(sessionController, roiPersistenceController),
                        new ImageViewerContextMenuCapability(_owner)));
            }
        }
    }
}
