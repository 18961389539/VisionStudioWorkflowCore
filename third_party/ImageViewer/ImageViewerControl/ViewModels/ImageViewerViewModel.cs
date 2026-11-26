using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using ImageViewer.Abstractions;
using ImageViewer.Common;
using ImageViewer.Core.Analysis;
using ImageViewer.Models;
using ImageViewer.Plugins;

namespace ImageViewer.ViewModels
{
    public partial class ImageViewerViewModel : BaseViewModel
    {
        /// <summary>
        /// 图像查看器视图模型
        /// Chinese: 封装 ImageViewer 控件的状态与数据，如 ROI 集合、缩放、偏移与显示选项。
        /// English: ViewModel containing state for the ImageViewer control such as ROIs, scale, offsets and display flags.
        /// </summary>

        private ImageSource? _imageSource;
        private readonly RoiCollectionStore _roiState;
        private double _scale = 1.0;
        private double _offsetX;
        private double _offsetY;
        private bool _showPixelGrid;
        private bool _showCrosshair;
        private bool _showInfoPanel;
        private string _infoText = string.Empty;
        private RoiBase? _selectedRoi;
        private readonly UndoRedoManager _undoRedoManager = new UndoRedoManager();
        private readonly ISelectedRoiDetectionService _selectedRoiDetectionService;
        private RoiPluginRegistry _pluginRegistry;
        private ImageAnalysisQualityProfile _qualityProfile = ImageAnalysisQualityProfile.Default;

        /// <summary>
        /// 尝试对当前选中的拟合直线 ROI 执行边缘检测。
        /// Chinese: Attempts to detect the true line for the currently selected line-caliper ROI.
        /// </summary>
        /// <param name="result">如果检测成功，返回检测结果。</param>
        /// <returns>检测成功返回 true，否则 false。</returns>
        public bool TryDetectSelectedLineCaliperEdges(out LineCaliperDetectionResult result)
        {
            return _selectedRoiDetectionService.TryDetectSelectedLineCaliperEdges(ImageSource, SelectedRoi, out result, QualityProfile);
        }

        public ImageAnalysisQualityProfile QualityProfile
        {
            get => _qualityProfile;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                value.Validate();
                _qualityProfile = value;
            }
        }

        public UndoRedoManager UndoRedo => _undoRedoManager;

        public ImageViewerViewModel(RoiPluginRegistry pluginRegistry, ISelectedRoiDetectionService? selectedRoiDetectionService = null)
        {
            _selectedRoiDetectionService = selectedRoiDetectionService ?? ImageViewer.Services.SelectedRoiDetectionService.Default;
            _pluginRegistry = pluginRegistry ?? throw new ArgumentNullException(nameof(pluginRegistry));
            _roiState = new RoiCollectionStore(EnumerateRois);
            _roiState.RebuildAllRois();
        }

        public RoiPluginRegistry PluginRegistry
        {
            get => _pluginRegistry;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                if (ReferenceEquals(_pluginRegistry, value))
                {
                    return;
                }

                _pluginRegistry = value;
                _roiState.RebuildAllRois();
            }
        }

        public ImageSource? ImageSource
        {
            get => _imageSource;
            set => SetProperty(ref _imageSource, value);
        }

        public RoiBase? SelectedRoi
        {
            get => _selectedRoi;
            set
            {
                if (ReferenceEquals(_selectedRoi, value))
                {
                    return;
                }

                if (_selectedRoi != null)
                {
                    _selectedRoi.IsSelected = false;
                }

                _selectedRoi = value;

                if (_selectedRoi != null)
                {
                    _selectedRoi.IsSelected = true;
                }

                OnPropertyChanged();
            }
        }

        public double Scale
        {
            get => _scale;
            set => SetProperty(ref _scale, value);
        }

        public double OffsetX
        {
            get => _offsetX;
            set => SetProperty(ref _offsetX, value);
        }

        public double OffsetY
        {
            get => _offsetY;
            set => SetProperty(ref _offsetY, value);
        }

        public bool ShowPixelGrid
        {
            get => _showPixelGrid;
            set => SetProperty(ref _showPixelGrid, value);
        }

        public bool ShowCrosshair
        {
            get => _showCrosshair;
            set => SetProperty(ref _showCrosshair, value);
        }

        public bool ShowInfoPanel
        {
            get => _showInfoPanel;
            set => SetProperty(ref _showInfoPanel, value);
        }

        public string InfoText
        {
            get => _infoText;
            set => SetProperty(ref _infoText, value);
        }

        public ObservableCollection<RoiBase> AllRois => _roiState.AllRois;

        /// <summary>
        /// 插件注册表变更时的状态迁移。
        /// Chinese: 一次性完成"丢弃按类型缓存的旧集合 → 迁移新注册表仍认识的 ROI → 处理选中项 → 清空撤销栈"。
        /// 不能只重建总列表：旧集合会让旧 ROI 被重新枚举回来，而撤销栈里的命令在插件缺失时只会静默失败。
        /// 返回因新注册表不认识其类型而被移除的 ROI（调用方负责提示/落盘处理）。
        /// English: Migrates ROI state to a changed plugin registry: drop the cached typed collections, re-add the ROIs
        /// whose type is still known, resolve selection, and clear the undo stack. Returns the ROIs dropped because the
        /// new registry no longer knows their type.
        /// </summary>
        public IReadOnlyList<RoiBase> ApplyPluginRegistryChange(RoiPluginRegistry pluginRegistry, IReadOnlyList<RoiBase> rois)
        {
            ArgumentNullException.ThrowIfNull(pluginRegistry);
            ArgumentNullException.ThrowIfNull(rois);

            _pluginRegistry = pluginRegistry;

            var migratable = new List<RoiBase>(rois.Count);
            var dropped = new List<RoiBase>();
            foreach (RoiBase roi in rois)
            {
                (pluginRegistry.FindByRoi(roi) != null ? migratable : dropped).Add(roi);
            }

            RoiBase? selectedRoi = SelectedRoi;
            bool selectionSurvives = selectedRoi != null && pluginRegistry.FindByRoi(selectedRoi) != null;

            _roiState.RunBatch(() =>
            {
                _roiState.Reset();

                foreach (RoiBase roi in migratable)
                {
                    AddRoiToTypedCollection(roi);
                }

                SelectedRoi = selectionSurvives ? selectedRoi : null;
            });

            UndoRedo.Clear();
            return dropped;
        }

        public ObservableCollection<T> GetRoiCollection<T>() where T : RoiBase
        {
            return _roiState.Get<T>();
        }

        public bool AddRoi(RoiBase roi)
        {
            ArgumentNullException.ThrowIfNull(roi);
            return AddRoiToTypedCollection(roi);
        }

        public bool RemoveRoi(RoiBase roi)
        {
            ArgumentNullException.ThrowIfNull(roi);
            return RemoveRoiFromTypedCollection(roi);
        }

        public void ClearAllRois()
        {
            _roiState.RunBatch(() =>
            {
                ClearTypedCollections();
                SelectedRoi = null;
            });
        }

        public void ReplaceAllRois(IEnumerable<RoiBase> rois)
        {
            ArgumentNullException.ThrowIfNull(rois);
            RoiBase[] materializedRois = rois.ToArray();
            _roiState.RunBatch(() =>
            {
                ClearTypedCollections();

                foreach (RoiBase roi in materializedRois)
                {
                    AddRoiToTypedCollection(roi);
                }

                SelectedRoi = null;
            });
        }

        private IEnumerable<RoiBase> EnumerateRois()
        {
            foreach (var plugin in PluginRegistry.Plugins)
            {
                foreach (var roi in plugin.GetRois(this))
                {
                    yield return roi;
                }
            }
        }

        private void ClearTypedCollections()
        {
            foreach (var plugin in PluginRegistry.Plugins)
            {
                plugin.ClearCollection(this);
            }
        }

        private bool AddRoiToTypedCollection(RoiBase roi)
        {
            return PluginRegistry.FindByRoi(roi)?.AddToCollection(this, roi) == true;
        }

        private bool RemoveRoiFromTypedCollection(RoiBase roi)
        {
            return PluginRegistry.FindByRoi(roi)?.RemoveFromCollection(this, roi) == true;
        }
    }
}
