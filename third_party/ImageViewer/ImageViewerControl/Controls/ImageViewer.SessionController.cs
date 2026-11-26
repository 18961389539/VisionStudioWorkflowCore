using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using ImageViewer.Abstractions;
using ImageViewer.Core.Analysis;
using ImageViewer.Core.Display;
using ImageViewer.Core.Geometry;
using ImageViewer.Core.Persistence;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Services;
using System.Windows.Threading;

namespace ImageViewer.Controls
{
    internal sealed class ImageViewerSessionControllerDependencies
    {
        public required ImageViewerSessionPersistenceWorkflow Persistence { get; init; }

        public required ImageViewerAutoSaveWorkflow AutoSave { get; init; }
    }

    internal sealed class ImageViewerSessionPersistenceWorkflow
    {
        public required Func<string?> ShowSaveSessionDialog { get; init; }

        public Func<MessageBoxResult> ConfirmUnsavedChanges { get; init; } = static () => MessageBoxResult.Cancel;

        public required Func<string?> ShowOpenSessionDialog { get; init; }

        public Func<string, string, string?> LocateSessionImage { get; init; } = static (_, _) => null;

        public required Func<string?> ShowSaveProjectPackageDialog { get; init; }

        public required Func<string?> ShowSaveRecipeDialog { get; init; }

        public required Func<string?> ShowOpenRecipeDialog { get; init; }

        public required IImageViewerSessionService SessionService { get; init; }

        public required IImageViewerRecentProjectService RecentProjectService { get; init; }

        public required IImageViewerProjectPackageService ProjectPackageService { get; init; }

        public required Func<RoiPluginRegistry> GetPluginRegistry { get; init; }

        public required Func<IReadOnlyList<RoiBase>> GetAllRois { get; init; }

        public required Func<double> GetPixelSize { get; init; }

        public required Action<double> SetPixelSize { get; init; }

        public required Func<string> GetPhysicalUnit { get; init; }

        public required Action<string> SetPhysicalUnit { get; init; }

        public required Func<CameraCalibration?> GetCalibration { get; init; }

        public Func<ImageAnalysisQualityProfile> GetQualityProfile { get; init; } = static () => ImageAnalysisQualityProfile.Default;

        public Action<ImageAnalysisQualityProfile> SetQualityProfile { get; init; } = static _ => { };

        public required Action<CameraCalibration?> SetCalibration { get; init; }

        /// <summary>
        /// 采集当前显示调节（随会话/项目包一起存取）。
        /// Chinese: 带默认值而不是 required —— 快照构造点分散（会话、项目包、恢复索引、测试替身），
        /// 未接线时退回"透传"，保存出来的就是一项不改变显示的设置，比编译不过或丢状态都好。
        /// </summary>
        public Func<DisplayAdjustment> GetDisplay { get; init; } = static () => DisplayAdjustment.Identity;

        public Action<DisplayAdjustment> SetDisplay { get; init; } = static _ => { };

        /// <summary>
        /// 当前工件坐标系（可为 null = 未定义，此时所有映射退化为恒等）。
        /// Chinese: 与 ROI 一样属于"当前测量方案"的一部分，随配方一起存取。
        /// </summary>
        public Func<WorkpieceCoordinateSystem?> GetWorkpieceCoordinateSystem { get; init; } = static () => null;

        public Action<WorkpieceCoordinateSystem?> SetWorkpieceCoordinateSystem { get; init; } = static _ => { };

        /// <summary>
        /// 保存配方时手动捕获的参考图像基准点，最多两个，顺序固定。
        /// </summary>
        public Func<IReadOnlyList<RecipeDatumPoint>> GetRecipeDatumPoints { get; init; } = static () => Array.Empty<RecipeDatumPoint>();

        public Func<IReadOnlyList<RecipeDatumPoint>> GetCurrentRecipeDatumPoints { get; init; } = static () => Array.Empty<RecipeDatumPoint>();

        public Func<object?> GetCurrentImageIdentity { get; init; } = static () => null;

        public Func<PointD?> GetSelectedRecipeDatumPosition { get; init; } = static () => null;

        public Action<int, RecipeDatumPoint> SetRecipeDatumPoint { get; init; } = static (_, _) => { };

        public Action<IReadOnlyList<RecipeDatumPoint>> SetRecipeDatumPoints { get; init; } = static _ => { };

        public Action<IReadOnlyList<RecipeDatumPoint>> SetCurrentRecipeDatumPoints { get; init; } = static _ => { };

        public Action<int, RecipeDatumPoint> SetCurrentRecipeDatumPoint { get; init; } = static (_, _) => { };

        public required Func<ImageViewerViewportState> GetCurrentViewportState { get; init; }

        public required Func<string?> TryGetCurrentImagePath { get; init; }

        public Func<BitmapSource?> GetCurrentBitmap { get; init; } = static () => null;

        public required Func<Func<CancellationToken, IProgress<double>, Task<Action?>>, Task<bool>> RunDocumentLoadAsync { get; init; }

        public required Func<string, string?, CancellationToken, IProgress<double>, Task<Action>> PrepareImageAsync { get; init; }

        public required Action ClearImage { get; init; }

        public Action EndDocumentInteraction { get; init; } = static () => { };

        public Action FitImageToView { get; init; } = static () => { };

        public required Action<IReadOnlyList<RoiBase>> ReplaceAllRois { get; init; }

        public required Action<ImageViewerViewportState> ApplyViewportState { get; init; }

        public required Action DrawRois { get; init; }

        public required Action<string, string, Exception> ShowNonCriticalError { get; init; }

        public required Action<string, string> ShowWarning { get; init; }

        public required Action<string, StatusHintKind> ShowStatusHint { get; init; }

        public required Action ClearUndoHistory { get; init; }

        public required Action UpdateContextMenuState { get; init; }

        /// <summary>
        /// 加载时未能识别的 ROI 载荷。
        /// Chinese: 打开工程时由会话控制器写入，保存（含自动保存）时原样回写，避免缺插件导致标注被抹掉。
        /// English: ROI payloads carried over from the last load so saves write them back verbatim.
        /// </summary>
        public IReadOnlyList<RoiPersistenceData> UnresolvedRois { get; set; } = [];

        /// <summary>
        /// Captures the document on the UI thread for session saves, package exports, and recovery snapshots.
        /// Converting ROIs to DTOs here lets the writer run off-thread without reading live editor state.
        /// </summary>
        public ImageViewerPersistenceSnapshot CaptureSnapshot()
        {
            ImageViewerViewportState viewportState = GetCurrentViewportState();
            var image = ImageViewerSnapshotImage.Capture(TryGetCurrentImagePath(), GetCurrentBitmap);
            return new ImageViewerPersistenceSnapshot(
                image.Path,
                RoiPersistenceService.CreateDocument(
                    GetAllRois(),
                    GetPixelSize(),
                    GetPhysicalUnit(),
                    GetPluginRegistry(),
                    UnresolvedRois,
                    GetQualityProfile()),
                viewportState.Scale,
                viewportState.TranslateX,
                viewportState.TranslateY,
                GetCalibration()?.Clone())
            {
                Display = GetDisplay(),
                CoordinateSystem = GetWorkpieceCoordinateSystem(),
                ReferenceDatumPoints = ImageViewerSessionService.CloneDatumPoints(GetRecipeDatumPoints()),
                CurrentDatumPoints = ImageViewerSessionService.CloneDatumPoints(GetCurrentRecipeDatumPoints()),
                InMemoryImage = image.Bitmap,
                ImageSha256 = image.Sha256
            };
        }
    }

    internal sealed class ImageViewerAutoSaveWorkflow
    {
        public required Dispatcher Dispatcher { get; init; }

        public required Func<bool> HasContent { get; init; }

        public required Action<string, Exception> LogNonCriticalError { get; init; }

        public required Action<string, StatusHintKind> ShowStatusHint { get; init; }
    }

    internal sealed class ImageViewerSessionController : IDisposable
    {
        private const string SessionProjectKind = "session";
        private const string PackageProjectKind = "package";
        private readonly ImageViewerSessionPersistenceWorkflow _persistence;
        private readonly ImageViewerAutoSaveWorkflow _autoSave;
        private readonly ImageViewerRecentProjectCatalog _recentProjectCatalog;
        private readonly ImageViewerAutoSaveController _autoSaveController;
        private readonly ImageViewerRecoveryIndex _recoveryIndex;
        private readonly string _autoSaveDirectory;
        private readonly string _sessionInstanceId;
        private string? _currentProjectPath;
        private string? _recoveryFilePath;
        private (string Path, DateTime LastWriteUtc)? _adoptedRecovery;
        private bool _recoveryPromptDismissed;
        private bool _isDirty;
        private long _editRevision;
        private bool _isConfirmingReplacement;

        /// <summary>
        /// 判定"某份快照是否仍被活动实例持有"的心跳窗口。
        /// Chinese: 自动保存每 <c>AutoSaveInterval</c> 刷新一次心跳，因此心跳只要在若干个间隔内就算"还活着"。
        /// 取 3 倍间隔、下限 30 秒，避免间隔极短（测试里 5s）时把仍在运行的窗口误判成已退出。
        /// </summary>
        private readonly TimeSpan _liveHeartbeatWindow;

        public event EventHandler? StateChanged;

        public ImageViewerSessionController(
            ImageViewerSessionControllerDependencies dependencies,
            IImageViewerPeriodicTaskSchedulerFactory periodicTaskSchedulerFactory,
            IImageViewerSessionStoragePolicy sessionStoragePolicy)
            : this(dependencies, periodicTaskSchedulerFactory, sessionStoragePolicy, Guid.NewGuid().ToString("N"))
        {
        }

        public ImageViewerSessionController(
            ImageViewerSessionControllerDependencies dependencies,
            IImageViewerPeriodicTaskSchedulerFactory periodicTaskSchedulerFactory,
            IImageViewerSessionStoragePolicy sessionStoragePolicy,
            string documentId)
        {
            ArgumentNullException.ThrowIfNull(dependencies);
            ArgumentNullException.ThrowIfNull(periodicTaskSchedulerFactory);
            ArgumentNullException.ThrowIfNull(sessionStoragePolicy);
            ArgumentException.ThrowIfNullOrWhiteSpace(documentId);

            _persistence = dependencies.Persistence ?? throw new ArgumentNullException(nameof(dependencies));
            _autoSave = dependencies.AutoSave;
            _autoSaveDirectory = sessionStoragePolicy.AutoSaveDirectory;
            _recoveryIndex = new ImageViewerRecoveryIndex(_autoSaveDirectory);
            _recentProjectCatalog = new ImageViewerRecentProjectCatalog(_persistence.RecentProjectService, sessionStoragePolicy.RecentProjectsFilePath);
            _autoSaveController = new ImageViewerAutoSaveController(
                _autoSave,
                _persistence.CaptureSnapshot,
                (path, snapshot, token) => _persistence.SessionService.SaveRecoveryToFileAsync(path, snapshot, _persistence.GetPluginRegistry(), token),
                periodicTaskSchedulerFactory, sessionStoragePolicy, documentId, _recoveryIndex);
            _sessionInstanceId = _autoSaveController.SessionInstanceId;
            _liveHeartbeatWindow = ComputeLiveHeartbeatWindow(sessionStoragePolicy.AutoSaveInterval);
            RefreshRecoverySnapshot();
        }

        public bool IsAutoSaveEnabled => _autoSaveController.IsEnabled;

        internal IDisposable SuspendAutoSaveWrites() => _autoSaveController.SuspendWrites();

        public bool IsDirty => _isDirty;

        internal string? CurrentProjectPath => _currentProjectPath;

        internal void RecordVolumeSliceNavigation() => _editRevision++;

        public bool HasRecoverySnapshot => !_recoveryPromptDismissed && _recoveryFilePath != null;

        public void MarkDirty()
        {
            _editRevision++;
            if (_isDirty)
            {
                return;
            }

            _isDirty = true;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void MarkClean()
        {
            if (!_isDirty)
            {
                return;
            }

            _isDirty = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void DismissRecoveryPrompt()
        {
            if (_recoveryPromptDismissed || _recoveryFilePath == null)
            {
                return;
            }

            _recoveryPromptDismissed = true;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task SaveSessionAsync() => SaveSessionAsync(_persistence.ShowSaveSessionDialog);

        internal async Task SaveSessionAsync(Func<string?> choosePath)
        {
            if (ShowInvalidToleranceDraft())
            {
                return;
            }

            try
            {
                string? filePath = choosePath();
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    return;
                }

                long savedRevision = _editRevision;
                await _persistence.SessionService.SaveToFileAsync(
                    filePath,
                    _persistence.CaptureSnapshot(),
                    _persistence.GetPluginRegistry());
                await _autoSaveController.DrainPendingWriteAsync();
                if (_editRevision == savedRevision)
                {
                    string previousAutoSave = _autoSaveController.GetAutoSaveFilePath();
                    SetCurrentProject(filePath, SessionProjectKind);
                    CompleteSuccessfulSave(previousAutoSave);
                }
                _persistence.ShowStatusHint(UiText.Get("StatusSaveSessionSuccess"), StatusHintKind.Success);
            }
            catch (Exception ex)
            {
                _persistence.ShowNonCriticalError(UiText.Get("ErrorSaveSessionTitle"),
                    UiText.Get(ex is ImageViewerSessionImageMismatchException ? "ErrorSaveChangedSessionImage" : "ErrorSaveSessionMessage"), ex);
            }
        }

        public async Task<bool> ConfirmDocumentReplacementAsync()
        {
            if (_isConfirmingReplacement)
            {
                return false;
            }
            if (!IsDirty)
            {
                return true;
            }

            _isConfirmingReplacement = true;
            try
            {
                switch (_persistence.ConfirmUnsavedChanges())
                {
                    case MessageBoxResult.No:
                        return true;
                    case MessageBoxResult.Yes:
                        await SaveSessionAsync();
                        return !IsDirty;
                    default:
                        return false;
                }
            }
            finally
            {
                _isConfirmingReplacement = false;
            }
        }

        internal Task<bool> ReplaceImageDocumentAsync(
            Func<CancellationToken, Task<Action?>> prepareContent, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(false);
            }

            return _persistence.RunDocumentLoadAsync(async (loadToken, _) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(loadToken, cancellationToken);
                try
                {
                    Action? apply = await PrepareImageDocumentAsync(prepareContent, markDirty: true, linked.Token);
                    return linked.IsCancellationRequested ? null : apply;
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    return null;
                }
            });
        }

        internal Task<Action?> PrepareImageDocumentAsync(Action applyImage, bool markDirty, CancellationToken cancellationToken) =>
            PrepareImageDocumentAsync(_ => Task.FromResult<Action?>(applyImage), markDirty, cancellationToken);

        internal async Task<Action?> PrepareImageDocumentAsync(
            Func<CancellationToken, Task<Action?>> prepareContent, bool markDirty, CancellationToken cancellationToken)
        {
            if (!await ConfirmSessionReplacementAsync(cancellationToken))
            {
                return null;
            }

            long revision = _editRevision;
            Action? applyImage = await prepareContent(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (applyImage == null || revision != _editRevision)
            {
                return null;
            }

            return () =>
            {
                string replacedAutoSave = _autoSaveController.GetAutoSaveFilePath();
                _persistence.EndDocumentInteraction();
                _persistence.ReplaceAllRois([]);
                _persistence.SetPixelSize(1);
                _persistence.SetPhysicalUnit("px");
                _persistence.SetCalibration(null);
                _persistence.SetDisplay(DisplayAdjustment.Identity);
                _persistence.SetQualityProfile(ImageAnalysisQualityProfile.Default);
                _persistence.SetWorkpieceCoordinateSystem(null);
                ReportUnresolvedRois([]);
                applyImage();
                _persistence.ApplyViewportState(ImageViewerViewportState.Default);
                _persistence.FitImageToView();
                _persistence.ClearUndoHistory();

                DeleteRecoverySnapshotSafely(replacedAutoSave);
                _currentProjectPath = null;
                _autoSaveController.SetCurrentProject(null);
                _adoptedRecovery = null;
                _persistence.DrawRois();
                _persistence.UpdateContextMenuState();
                if (markDirty)
                {
                    MarkDirty();
                }
                else
                {
                    MarkClean();
                }
            };
        }

        public async Task LoadProjectAsync()
        {
            string? filePath = _persistence.ShowOpenSessionDialog();
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            await OpenProjectAsync(filePath);
        }

        public async Task ExportProjectPackageAsync()
        {
            if (ShowInvalidToleranceDraft())
            {
                return;
            }

            string? filePath = _persistence.ShowSaveProjectPackageDialog();
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            try
            {
                long savedRevision = _editRevision;
                await _persistence.ProjectPackageService.ExportAsync(
                    filePath,
                    _persistence.CaptureSnapshot(),
                    _persistence.GetPluginRegistry());
                await _autoSaveController.DrainPendingWriteAsync();
                if (_editRevision == savedRevision)
                {
                    string previousAutoSave = _autoSaveController.GetAutoSaveFilePath();
                    SetCurrentProject(filePath, PackageProjectKind);
                    CompleteSuccessfulSave(previousAutoSave);
                }
                _persistence.ShowStatusHint(UiText.Get("StatusExportPackageSuccess"), StatusHintKind.Success);
            }
            catch (Exception ex)
            {
                _persistence.ShowNonCriticalError(UiText.Get("ErrorExportProjectPackageTitle"),
                    UiText.Get(ex is ImageViewerSessionImageMismatchException ? "ErrorSaveChangedSessionImage" : "ErrorExportProjectPackageMessage"), ex);
            }
        }

        public IReadOnlyList<ImageViewerDynamicMenuItem> GetRecentProjectMenuItems()
        {
            return _recentProjectCatalog.GetMenuItems();
        }

        public Task OpenRecentProjectAsync(string filePath)
        {
            return OpenProjectAsync(filePath);
        }

        /// <summary>
        /// 把当前测量方案保存为配方（工件坐标系 + 基准点 + 标定 + 测量项）。
        /// Chinese: 配方与"会话/项目包"的区别是**可交付、可复现**——它刻意不带图像路径与视口状态，
        /// 以便同一个测量方案用在任意一张新图上。也因此保存后不改"当前项目"，避免把配方误当成会话。
        /// English: A recipe is a deliverable plan, not a session: it carries no image path or viewport,
        /// so it can be reused on any new image.
        /// </summary>
        public async Task SaveRecipeAsync()
        {
            if (ShowInvalidToleranceDraft())
            {
                return;
            }

            string? filePath = _persistence.ShowSaveRecipeDialog();
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            try
            {
                IReadOnlyList<RoiBase> rois = _persistence.GetAllRois();
                if (rois.Count == 0)
                {
                    // 没有测量项的配方没有复现价值；提示而不是写出一个空文件让人以为成功了。
                    _persistence.ShowStatusHint(UiText.Get("StatusRecipeNoRois"), StatusHintKind.Info);
                    return;
                }

                MeasurementRecipeDocument recipe = MeasurementRecipeService.Create(
                    name: Path.GetFileNameWithoutExtension(filePath),
                    description: null,
                    rois: rois,
                    pixelSize: _persistence.GetPixelSize(),
                    physicalUnit: _persistence.GetPhysicalUnit(),
                    pluginRegistry: _persistence.GetPluginRegistry(),
                    coordinateSystem: _persistence.GetWorkpieceCoordinateSystem(),
                    datumPoints: _persistence.GetRecipeDatumPoints(),
                    calibration: _persistence.GetCalibration(),
                    qualityProfile: _persistence.GetQualityProfile());

                await MeasurementRecipeService.SaveToFileAsync(filePath, recipe).ConfigureAwait(true);
                _persistence.ShowStatusHint(
                    UiText.Format("StatusRecipeSaveSuccess", recipe.EnabledItemCount, Path.GetFileName(filePath)),
                    StatusHintKind.Success);
            }
            catch (Exception ex)
            {
                _persistence.ShowNonCriticalError(
                    UiText.Get("ErrorSaveRecipeTitle"),
                    UiText.Get("ErrorSaveRecipeMessage"),
                    ex);
            }
        }

        private bool ShowInvalidToleranceDraft()
        {
            if (!RoiToleranceDrafts.TryGetError(_persistence.GetAllRois(), out string? message))
            {
                return false;
            }

            _persistence.ShowStatusHint(message, StatusHintKind.Error);
            return true;
        }

        public void CaptureRecipeDatumPoint(int index, bool currentImage)
        {
            if (index is < 0 or > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            if (_persistence.GetSelectedRecipeDatumPosition() is not PointD position)
            {
                _persistence.ShowStatusHint(UiText.Get("StatusRecipeDatumSelectPoint"), StatusHintKind.Error);
                return;
            }

            var point = new RecipeDatumPoint
            {
                Name = index == 0 ? "Datum 1" : "Datum 2",
                X = position.X,
                Y = position.Y
            };
            if (currentImage)
            {
                _persistence.SetCurrentRecipeDatumPoint(index, point);
                _persistence.ShowStatusHint(
                    UiText.Format("StatusRecipeCurrentDatumCaptured", index + 1, position.X, position.Y),
                    StatusHintKind.Success);
            }
            else
            {
                _persistence.SetRecipeDatumPoint(index, point);
                _persistence.ShowStatusHint(
                    UiText.Format("StatusRecipeReferenceDatumCaptured", index + 1, position.X, position.Y),
                    StatusHintKind.Success);
            }
            MarkDirty();
        }

        /// <summary>
        /// 载入配方并应用其测量项、标定与坐标系。
        /// Chinese: 载入后立刻报一次可判定性——"载入配方 ≠ 可以出判定"，缺标定/缺公差必须当场说清楚，
        /// 否则操作员会以为配方一切就绪、直接开始出报告。
        /// </summary>
        public async Task LoadRecipeAsync()
        {
            await LoadRecipeAsync(applyAlignment: false).ConfigureAwait(true);
        }

        public async Task LoadAlignedRecipeAsync()
        {
            await LoadRecipeAsync(applyAlignment: true).ConfigureAwait(true);
        }

        private async Task LoadRecipeAsync(bool applyAlignment)
        {
            string? filePath = _persistence.ShowOpenRecipeDialog();
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            try
            {
                await _persistence.RunDocumentLoadAsync((token, _) => PrepareRecipeAsync(filePath, applyAlignment, token));
            }
            catch (Exception ex)
            {
                if (applyAlignment && ex is InvalidOperationException or NotSupportedException or ArgumentOutOfRangeException)
                {
                    _persistence.ShowStatusHint(UiText.Format("StatusRecipeAlignmentFailed", ex.Message), StatusHintKind.Error);
                    return;
                }

                _persistence.ShowNonCriticalError(
                    UiText.Get("ErrorLoadRecipeTitle"),
                    UiText.Get("ErrorLoadRecipeMessage"),
                    ex);
            }
        }

        private async Task<Action?> PrepareRecipeAsync(string filePath, bool applyAlignment, CancellationToken cancellationToken)
        {
            long revision = _editRevision;
            object? sourceIdentity = _persistence.GetCurrentImageIdentity();
            IReadOnlyList<RecipeDatumPoint> currentDatumPoints = applyAlignment
                ? _persistence.GetCurrentRecipeDatumPoints()
                    .Select(point => new RecipeDatumPoint { Name = point.Name, X = point.X, Y = point.Y })
                    .ToArray()
                : [];

            MeasurementRecipeDocument recipe = await MeasurementRecipeService.LoadFromFileAsync(filePath, cancellationToken);
            if (!CanApply())
            {
                return null;
            }

            if (applyAlignment && (!recipe.CanLocalize || !HasDistinctPair(recipe.DatumPoints) || !HasDistinctPair(currentDatumPoints)))
            {
                _persistence.ShowStatusHint(UiText.Get("StatusRecipeAlignmentDatumsRequired"), StatusHintKind.Error);
                return null;
            }

            RoiDocument roiDocument;
            WorkpieceCoordinateSystem? coordinateSystem = recipe.CoordinateSystem;
            IReadOnlyList<RecipeDatumPoint> referenceDatumPoints;
            if (applyAlignment)
            {
                PointD current1 = new(currentDatumPoints[0].X, currentDatumPoints[0].Y);
                PointD current2 = new(currentDatumPoints[1].X, currentDatumPoints[1].Y);
                roiDocument = MeasurementRecipeService.BuildAlignedRoiDocument(recipe, current1, current2);
                coordinateSystem = WorkpieceCoordinateSystem.Localize(current1, current2,
                    new PointD(recipe.DatumPoints[0].X, recipe.DatumPoints[0].Y),
                    new PointD(recipe.DatumPoints[1].X, recipe.DatumPoints[1].Y), recipe.CoordinateSystem?.Name);
                referenceDatumPoints = currentDatumPoints;
            }
            else
            {
                roiDocument = MeasurementRecipeService.BuildRoiDocument(recipe);
                referenceDatumPoints = ImageViewerSessionService.CloneDatumPoints(recipe.DatumPoints);
            }

            RoiDocumentLoadResult restored = RoiPersistenceService.CreateRois(roiDocument, _persistence.GetPluginRegistry());
            if (restored.UnresolvedItems.Count > 0)
            {
                string types = string.Join(", ", restored.UnresolvedItems.Select(item => item.Type).Distinct(StringComparer.OrdinalIgnoreCase));
                _persistence.ShowStatusHint(UiText.Format("StatusRecipeUnsupportedRoiCount", restored.UnresolvedItems.Count, types), StatusHintKind.Error);
                return null;
            }

            MeasurementWorkflowReadiness readiness = MeasurementRecipeService.EvaluateReadiness(recipe);
            if (!CanApply() || !await ConfirmDocumentReplacementAsync() || !CanApply())
            {
                return null;
            }

            return () =>
            {
                if (!CanApply())
                {
                    return;
                }

                if (restored.QualityProfile is { } qualityProfile)
                {
                    _persistence.SetQualityProfile(qualityProfile);
                }

                _persistence.ReplaceAllRois(restored.Rois);
                _persistence.SetPixelSize(recipe.PixelSize);
                if (!string.IsNullOrWhiteSpace(recipe.PhysicalUnit))
                {
                    _persistence.SetPhysicalUnit(restored.PhysicalUnit);
                }

                _persistence.SetCalibration(recipe.Calibration);
                _persistence.SetWorkpieceCoordinateSystem(coordinateSystem);
                _persistence.SetRecipeDatumPoints(referenceDatumPoints);
                ReportUnresolvedRois([]);
                _persistence.ClearUndoHistory();
                MarkDirty();
                _persistence.DrawRois();
                _persistence.UpdateContextMenuState();

                StatusHintKind kind = readiness.IsBlocked
                    ? StatusHintKind.Error
                    : readiness.HasWarnings ? StatusHintKind.Info : StatusHintKind.Success;
                _persistence.ShowStatusHint(
                    UiText.Format("StatusRecipeLoadSuccess", restored.Rois.Count, MeasurementWorkflowReadinessFormatter.FormatSingleLine(readiness))
                        + (applyAlignment ? " " + UiText.Get("StatusRecipeManualAlignmentApplied") : string.Empty),
                    kind);
            };

            bool CanApply()
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (revision == _editRevision && ReferenceEquals(sourceIdentity, _persistence.GetCurrentImageIdentity()))
                {
                    return true;
                }

                _persistence.ShowStatusHint(UiText.Get("StatusRecipeTargetChanged"), StatusHintKind.Error);
                return false;
            }
        }

        private static bool HasDistinctPair(IReadOnlyList<RecipeDatumPoint> points)
        {
            return points.Count == 2
                && double.IsFinite(points[0].X) && double.IsFinite(points[0].Y)
                && double.IsFinite(points[1].X) && double.IsFinite(points[1].Y)
                && (Math.Abs(points[1].X - points[0].X) > 1e-12
                    || Math.Abs(points[1].Y - points[0].Y) > 1e-12);
        }

        public Task RecoverLatestAutoSaveAsync() => RecoverAutoSaveAsync(chooseSnapshot: null);

        public async Task RecoverAutoSaveAsync(Func<IReadOnlyList<RecoverySnapshotOption>, RecoverySnapshotOption?>? chooseSnapshot)
        {
            IReadOnlyList<RecoverySnapshotOption> candidates = GetRecoveryCandidates();
            _recoveryFilePath = candidates.Count == 0 ? null : candidates[0].Path;
            if (candidates.Count == 0)
            {
                StateChanged?.Invoke(this, EventArgs.Empty);
                _persistence.ShowStatusHint(UiText.Get("StatusNoRecoverySnapshot"), StatusHintKind.Info);
                return;
            }

            bool explicitlySelected = chooseSnapshot != null;
            if (chooseSnapshot != null)
            {
                RecoverySnapshotOption? selected = chooseSnapshot(candidates);
                if (selected == null || !candidates.Contains(selected))
                {
                    return;
                }

                // The snapshot may have changed while the picker was open.
                if (!GetRecoveryCandidates().Contains(selected))
                {
                    RefreshRecoverySnapshot();
                    StateChanged?.Invoke(this, EventArgs.Empty);
                    _persistence.ShowStatusHint(UiText.Get("StatusRecoverySnapshotChanged"), StatusHintKind.Info);
                    return;
                }
                candidates = [selected];
            }

            await _persistence.RunDocumentLoadAsync((token, progress) => PrepareRecoveryAsync(candidates, explicitlySelected, token, progress));
        }

        private async Task<Action?> PrepareRecoveryAsync(
            IReadOnlyList<RecoverySnapshotOption> recoveryCandidates,
            bool explicitlySelected,
            CancellationToken cancellationToken,
            IProgress<double> progress)
        {
            Exception? lastLoadException = null;
            string? imageProblem = null;
            foreach (RecoverySnapshotOption candidate in recoveryCandidates)
            {
                string recoveryFilePath = candidate.Path;
                try
                {
                    DateTime snapshotWriteTime = File.GetLastWriteTimeUtc(recoveryFilePath);
                    if (explicitlySelected && snapshotWriteTime != candidate.LastWriteUtc)
                    {
                        RefreshRecoverySnapshot();
                        StateChanged?.Invoke(this, EventArgs.Empty);
                        _persistence.ShowStatusHint(UiText.Get("StatusRecoverySnapshotChanged"), StatusHintKind.Info);
                        return null;
                    }
                    ImageViewerSessionData session = await _persistence.SessionService.LoadFromFileAsync(
                        recoveryFilePath,
                        _persistence.GetPluginRegistry(), cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    var (applyImage, _, problem) = await PrepareSessionImageAsync(
                        session, recoveryFilePath, explicitlySelected, cancellationToken, progress);
                    if (applyImage == null)
                    {
                        if (explicitlySelected)
                        {
                            _persistence.ShowStatusHint(problem!, StatusHintKind.Error);
                            return null;
                        }
                        imageProblem ??= problem;
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    if (!await ConfirmSessionReplacementAsync(cancellationToken))
                    {
                        return null;
                    }
                    if (explicitlySelected && File.GetLastWriteTimeUtc(recoveryFilePath) != snapshotWriteTime)
                    {
                        RefreshRecoverySnapshot();
                        StateChanged?.Invoke(this, EventArgs.Empty);
                        _persistence.ShowStatusHint(UiText.Get("StatusRecoverySnapshotChanged"), StatusHintKind.Info);
                        return null;
                    }
                    return () =>
                    {
                        string replacedAutoSave = _autoSaveController.GetAutoSaveFilePath();
                        ApplySession(session, applyImage);
                        DeleteRecoverySnapshotSafely(replacedAutoSave);
                        _currentProjectPath = null;
                        _autoSaveController.SetCurrentProject(null);
                        _adoptedRecovery = (recoveryFilePath, snapshotWriteTime);
                        _persistence.ClearUndoHistory();
                        ReportUnresolvedRois(session.UnresolvedRois);
                        _recoveryPromptDismissed = false;
                        RefreshRecoverySnapshot();
                        StateChanged?.Invoke(this, EventArgs.Empty);
                        MarkDirty();
                        _persistence.ShowStatusHint(UiText.Get("StatusRecoveryLoaded"), StatusHintKind.Success);
                    };
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    lastLoadException = ex;
                    _autoSave.LogNonCriticalError("Load autosave candidate", ex);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            _recoveryFilePath = null;
            if (explicitlySelected)
            {
                RefreshRecoverySnapshot();
            }
            _recoveryPromptDismissed = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
            if (lastLoadException != null)
            {
                _persistence.ShowNonCriticalError(
                    UiText.Get("ErrorRecoveryTitle"),
                    UiText.Get("ErrorRecoveryMessage"),
                    lastLoadException);
            }
            else if (imageProblem != null)
            {
                _persistence.ShowStatusHint(imageProblem, StatusHintKind.Error);
            }
            else
            {
                _persistence.ShowStatusHint(UiText.Get("StatusNoRecoverySnapshot"), StatusHintKind.Info);
            }

            return null;
        }

        public void ToggleAutoSave()
        {
            _autoSaveController.Toggle();
        }

        public void StartAutoSave()
        {
            _autoSaveController.Start();
        }

        public void StopAutoSave()
        {
            _autoSaveController.StopScheduling();
        }

        public Task DrainAutoSaveAsync(CancellationToken cancellationToken = default)
        {
            return _autoSaveController.DrainAsync(cancellationToken);
        }

        public void Dispose() => _autoSaveController.Dispose();

        public Task CloseCompletion => _autoSaveController.CloseCompletion;

        public Task<bool> OpenProjectAsync(string filePath) => OpenProjectAsync(filePath, null);

        internal async Task<bool> OpenProjectAsync(
            string filePath, Func<CancellationToken, Task<Action?>>? prepareRelatedContent)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    _recentProjectCatalog.RemoveMissing(filePath);
                    _persistence.ShowWarning(UiText.Get("WarningFileMissingTitle"), UiText.Get("WarningRecentProjectRemoved"));
                    _persistence.UpdateContextMenuState();
                    return false;
                }

                return await _persistence.RunDocumentLoadAsync(async (token, progress) =>
                {
                    bool isPackage = string.Equals(Path.GetExtension(filePath), ".ivpkg", StringComparison.OrdinalIgnoreCase);
                    ImageViewerSessionData session = isPackage
                        ? await _persistence.ProjectPackageService.LoadAsync(filePath, _persistence.GetPluginRegistry(), token)
                        : await _persistence.SessionService.LoadFromFileAsync(filePath, _persistence.GetPluginRegistry(), token);
                    token.ThrowIfCancellationRequested();
                    var (applyImage, relinked, problem) = await PrepareSessionImageAsync(session, filePath, !isPackage, token, progress);
                    if (applyImage == null)
                    {
                        _persistence.ShowStatusHint(problem!, StatusHintKind.Error);
                        return null;
                    }

                    token.ThrowIfCancellationRequested();
                    if (!await ConfirmSessionReplacementAsync(token))
                    {
                        return null;
                    }
                    Action? applyRelatedContent = null;
                    if (prepareRelatedContent != null)
                    {
                        long revision = _editRevision;
                        applyRelatedContent = await prepareRelatedContent(token);
                        token.ThrowIfCancellationRequested();
                        if (applyRelatedContent == null || revision != _editRevision)
                        {
                            return null;
                        }
                    }
                    return () =>
                    {
                        string replacedAutoSave = _autoSaveController.GetAutoSaveFilePath();
                        ApplySession(session, applyImage);
                        applyRelatedContent?.Invoke();
                        DeleteRecoverySnapshotSafely(replacedAutoSave);
                        _adoptedRecovery = null;
                        SetCurrentProject(filePath, isPackage ? PackageProjectKind : SessionProjectKind);
                        _persistence.ClearUndoHistory();
                        if (relinked)
                        {
                            MarkDirty();
                        }
                        else
                        {
                            MarkClean();
                        }
                        _persistence.ShowStatusHint(UiText.Get(relinked ? "StatusSessionImageRelinked" : "StatusLoadSessionSuccess"), StatusHintKind.Success);
                        ReportUnresolvedRois(session.UnresolvedRois);
                    };
                });
            }
            catch (Exception ex)
            {
                _persistence.ShowNonCriticalError(UiText.Get("ErrorLoadProjectTitle"), UiText.Get("ErrorLoadProjectMessage"), ex);
                return false;
            }
        }

        private async Task<(Action? Apply, bool Relinked, string? Problem)> PrepareSessionImageAsync(
            ImageViewerSessionData session, string sessionPath, bool allowRelink,
            CancellationToken cancellationToken, IProgress<double> progress)
        {
            if (string.IsNullOrWhiteSpace(session.ImagePath))
            {
                return (_persistence.ClearImage, false, null);
            }

            string path = session.ImagePath;
            string problem = UiText.Format("StatusSessionImageMissing", path);
            if (File.Exists(path))
            {
                try
                {
                    return (await _persistence.PrepareImageAsync(path, session.ImageSha256, cancellationToken, progress), false, null);
                }
                catch (ImageViewerSessionImageMismatchException)
                {
                    problem = UiText.Format("StatusSessionImageMismatch", path);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    // The file may have been moved between the existence check and the open.
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!allowRelink)
            {
                return (null, false, problem);
            }

            string? selected = _persistence.LocateSessionImage(path, Path.GetFullPath(sessionPath));
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(selected))
            {
                return (null, false, problem);
            }

            selected = Path.GetFullPath(selected);
            try
            {
                return (await _persistence.PrepareImageAsync(selected, session.ImageSha256, cancellationToken, progress), true, null);
            }
            catch (ImageViewerSessionImageMismatchException)
            {
                return (null, false, UiText.Format("StatusSessionImageMismatch", selected));
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return (null, false, UiText.Format("StatusSessionImageMissing", selected));
            }
        }

        private async Task<bool> ConfirmSessionReplacementAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await ConfirmDocumentReplacementAsync())
            {
                return false;
            }

            long revision = _editRevision;
            await _autoSaveController.DrainPendingWriteAsync().WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return revision == _editRevision;
        }

        private void ApplySession(ImageViewerSessionData session, Action applyImage)
        {
            _persistence.EndDocumentInteraction();
            applyImage();
            _persistence.SetWorkpieceCoordinateSystem(session.CoordinateSystem);
            _persistence.SetRecipeDatumPoints(session.ReferenceDatumPoints);
            _persistence.SetCurrentRecipeDatumPoints(session.CurrentDatumPoints);
            _persistence.ReplaceAllRois(session.Rois);
            _persistence.SetPixelSize(session.PixelSize);
            _persistence.SetPhysicalUnit(session.PhysicalUnit);
            _persistence.SetQualityProfile(session.QualityProfile);
            _persistence.SetCalibration(session.Calibration);
            _persistence.SetDisplay(session.Display);
            _persistence.ApplyViewportState(new ImageViewerViewportState(session.Scale, session.TranslateX, session.TranslateY));
            _persistence.DrawRois();
            _persistence.UpdateContextMenuState();
        }

        private void SetCurrentProject(string filePath, string projectKind)
        {
            _currentProjectPath = Path.GetFullPath(filePath);
            _autoSaveController.SetCurrentProject(_currentProjectPath);
            try
            {
                _recentProjectCatalog.Remember(_currentProjectPath, projectKind);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _autoSave.LogNonCriticalError("Update recent projects", ex);
            }

            _persistence.UpdateContextMenuState();
        }

        private void CompleteSuccessfulSave(string previousAutoSave)
        {
            MarkClean();
            var adopted = _adoptedRecovery;
            _adoptedRecovery = null;

            try
            {
                DeleteRecoverySnapshot(previousAutoSave);
                // The offered recovery is only a suggestion. Clean it up only if the user
                // actually opened that snapshot and it has not changed since it was read.
                if (adopted is { } snapshot &&
                    !string.Equals(previousAutoSave, snapshot.Path, StringComparison.OrdinalIgnoreCase) &&
                    File.GetLastWriteTimeUtc(snapshot.Path) == snapshot.LastWriteUtc)
                {
                    DeleteRecoverySnapshot(snapshot.Path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _persistence.ShowStatusHint(UiText.Get("StatusRecoveryCleanupFailed"), StatusHintKind.Info);
                _autoSave.LogNonCriticalError("Clean up recovered autosave", ex);
            }

            if (adopted != null)
            {
                _recoveryPromptDismissed = false;
            }
            RefreshRecoverySnapshot();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void DeleteRecoverySnapshot(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            ImageViewerSnapshotImage.PruneRecoveryImages(path);
            _recoveryIndex.Unregister(path);
        }

        private void DeleteRecoverySnapshotSafely(string path)
        {
            try
            {
                DeleteRecoverySnapshot(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _autoSave.LogNonCriticalError("Clean up replaced autosave", ex);
            }
        }

        private void RefreshRecoverySnapshot()
        {
            IReadOnlyList<RecoverySnapshotOption> candidates = GetRecoveryCandidates();
            _recoveryFilePath = candidates.Count == 0 ? null : candidates[0].Path;
        }

        private IReadOnlyList<RecoverySnapshotOption> GetRecoveryCandidates()
        {
            if (!Directory.Exists(_autoSaveDirectory))
            {
                return [];
            }

            try
            {
                string[] candidates = Directory
                    .EnumerateFiles(_autoSaveDirectory, "*.ivsession", SearchOption.TopDirectoryOnly)
                    .Where(path =>
                    {
                        string fileName = Path.GetFileName(path);
                        return !fileName.StartsWith('.') &&
                               !fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
                               HasContent(path);
                    })
                    .OrderByDescending(GetLastWriteTimeUtc)
                    .ToArray();

                // The index distinguishes abandoned snapshots from snapshots of open windows, including this one.
                return GetIndexedRecoveryCandidates(candidates);
            }
            catch
            {
                return [];
            }
        }

        /// <summary>
        /// 从磁盘上的候选里挑出被恢复索引登记过、且**不再被活动实例持有**的那些（保持原有 mtime 倒序）。
        /// Chinese: 索引是"本机遗留的未保存文档"的清单。两类条目不该出现在候选里：
        /// ① 已正常保存过的文档（成功保存时已从索引摘除）；
        /// ② 仍被某个活着的窗口持有的文档——它只是"另一个窗口正开着"，不是"待恢复的遗留文档"。
        /// 后者靠 <see cref="ImageViewerRecoveryIndexEntry.OwnerInstanceId"/> + 新鲜心跳识别，
        /// 因此崩溃/强杀的实例（心跳过期）仍然可恢复。
        /// </summary>
        private RecoverySnapshotOption[] GetIndexedRecoveryCandidates(string[] onDiskCandidates)
        {
            IReadOnlyList<ImageViewerRecoveryIndexEntry> indexedEntries = _recoveryIndex.Prune() ?? [];
            DateTimeOffset heartbeatThreshold = DateTimeOffset.UtcNow - _liveHeartbeatWindow;
            var byPath = new Dictionary<string, ImageViewerRecoveryIndexEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (ImageViewerRecoveryIndexEntry entry in indexedEntries)
            {
                byPath.TryAdd(entry.Path, entry);
            }

            var choices = new List<RecoverySnapshotOption>();
            foreach (string path in onDiskCandidates)
            {
                string fullPath = Path.GetFullPath(path);
                DateTime savedAt = GetLastWriteTimeUtc(fullPath);
                if (_adoptedRecovery is { } adopted &&
                    string.Equals(adopted.Path, fullPath, StringComparison.OrdinalIgnoreCase) &&
                    adopted.LastWriteUtc == savedAt)
                {
                    continue;
                }

                if (byPath.TryGetValue(fullPath, out ImageViewerRecoveryIndexEntry? entry))
                {
                    if (IsRecoverable(entry, heartbeatThreshold))
                    {
                        choices.Add(new(fullPath, savedAt, entry.ProjectName, entry.ImageName));
                    }
                }
                else if (IsLegacyUnidentifiedAutoSave(Path.GetFileName(fullPath)))
                {
                    choices.Add(new(fullPath, savedAt, null, null));
                }
            }
            return [.. choices];
        }

        /// <summary>
        /// 这份快照是否可被本窗口恢复。
        /// Chinese: 同一实例（自己的）不是"要恢复的遗留文档"；其它实例只要心跳还新鲜就视为"仍在使用"。
        /// 没有所有者信息的历史条目（升级前写的）没有心跳可比，按"可恢复"处理以保住存量数据。
        /// English: A snapshot is recoverable when it is not owned by this instance and its owner's heartbeat
        /// is stale. Legacy entries without ownership info are treated as recoverable.
        /// </summary>
        private bool IsRecoverable(ImageViewerRecoveryIndexEntry entry, DateTimeOffset heartbeatThreshold)
        {
            if (string.Equals(entry.OwnerInstanceId, _sessionInstanceId, StringComparison.Ordinal) ||
                _recoveryIndex.IsOwnerActive(entry.OwnerInstanceId))
            {
                return false;
            }

            if (entry.HasOwnerLease || entry.LastHeartbeatUtc is not DateTimeOffset heartbeat)
            {
                // A lease that was recorded and is now gone proves its owner is no longer running.
                return true;
            }

            // 心跳落在阈值之内 = 写它的窗口还活着（或刚刚才写），不该提示恢复。
            return heartbeat < heartbeatThreshold;
        }

        private static TimeSpan ComputeLiveHeartbeatWindow(TimeSpan autoSaveInterval)
        {
            TimeSpan window = autoSaveInterval <= TimeSpan.Zero
                ? TimeSpan.FromSeconds(30)
                : autoSaveInterval * 3;
            return window < TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : window;
        }

        /// <summary>
        /// 旧版遗留的、不带文档身份信息的自动保存文件名。
        /// Chinese: 仅用于升级兜底——过去所有未保存项目都写同一个 "autosave.ivsession"。
        /// 一旦本窗口产生了自己的候选，就不再回退到这些共享文件，避免多窗口互相抢占。
        /// English: Legacy shared names kept only as an upgrade fallback (older builds wrote every unsaved
        /// document to "autosave.ivsession"). Once this window has its own candidate we stop falling back.
        /// </summary>
        private static bool IsLegacyUnidentifiedAutoSave(string fileName)
        {
            return string.Equals(fileName, "autosave.ivsession", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasContent(string filePath)
        {
            try
            {
                return new FileInfo(filePath).Length > 0;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static DateTime GetLastWriteTimeUtc(string filePath)
        {
            try
            {
                return File.GetLastWriteTimeUtc(filePath);
            }
            catch (IOException)
            {
                return DateTime.MinValue;
            }
            catch (UnauthorizedAccessException)
            {
                return DateTime.MinValue;
            }
        }

        /// <summary>
        /// 上报并对齐"未识别标注"载荷。
        /// Chinese: 缺插件或类型键变更时，未识别的标注不再被静默丢弃——状态栏给出数量与类型提示，
        /// 同时把原始载荷保留在保存链路上，下次保存会原样回写，重新获得插件后仍可正常打开。
        /// English: Surfaces unresolved ROI payloads (missing plugin / renamed type key) with a status hint and keeps
        /// them on the save path so the next save writes them back verbatim instead of erasing them.
        /// </summary>
        /// <summary>
        /// 追加未识别 ROI 载荷（不替换已有载荷）。
        /// Chinese: 插件卸载会把被移除的 ROI 序列化成 unresolved 载荷追加到这里；加载会话时 ReportUnresolvedRois
        /// 是整体替换，两者语义不同——替换会抹掉之前追加的载荷。
        /// English: Appends payloads without discarding the ones already carried from the last load.
        /// </summary>
        internal void AppendUnresolvedRois(IReadOnlyList<RoiPersistenceData> payloads)
        {
            if (payloads.Count == 0)
            {
                return;
            }

            var merged = new List<RoiPersistenceData>(_persistence.UnresolvedRois);
            merged.AddRange(payloads);
            _persistence.UnresolvedRois = merged;
        }

        private void ReportUnresolvedRois(IReadOnlyList<RoiPersistenceData> unresolvedRois)
        {
            _persistence.UnresolvedRois = unresolvedRois;
            if (unresolvedRois.Count == 0)
            {
                return;
            }

            string typeNames = string.Join(
                ", ",
                unresolvedRois
                    .Select(item => item.Type)
                    .Where(type => !string.IsNullOrWhiteSpace(type))
                    .Distinct(StringComparer.OrdinalIgnoreCase));

            _persistence.ShowStatusHint(
                UiText.Format("StatusProjectUnresolvedRois", unresolvedRois.Count, typeNames),
                StatusHintKind.Error);
        }
    }
}
