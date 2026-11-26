#pragma warning disable CS1591
using System;
using ImageViewer.Common;
using ImageViewer.Core.Measurements;

namespace ImageViewer.Models
{
    /// <summary>
    /// ROI 基类
    /// Chinese: 表示所有 ROI（感兴趣区域）对象的基类，包含共享属性如标签、边框颜色、粗细、可见性等。
    /// English: Base class for all ROI (Region of Interest) objects. Provides common properties such as
    /// Label, StrokeColor, StrokeThickness, visibility and locking.
    /// </summary>
    public abstract class RoiBase : BaseViewModel
    {
        private string _label = string.Empty;
        private RoiColor _strokeColor = RoiColors.Cyan;
        private double _strokeThickness = 2.0;
        private bool _isSelected;
        private bool _isVisible = true;
        private bool _isLocked;
        private MeasurementTolerance? _tolerance;
        private Guid _measurementId = Guid.NewGuid();
        private AdvancedMeasurementDefinition[] _advancedMeasurements = [];

        /// <summary>Stable identity used to associate advanced measurements with this ROI.</summary>
        public Guid MeasurementId
        {
            get => _measurementId;
            set => SetProperty(ref _measurementId, value == Guid.Empty ? Guid.NewGuid() : value);
        }

        /// <summary>Gets or sets the advanced measurements owned by this ROI.</summary>
        public AdvancedMeasurementDefinition[] AdvancedMeasurements
        {
            get => _advancedMeasurements;
            set => SetProperty(ref _advancedMeasurements, value?.Where(item => item is not null)
                .Select(item => item with { Sources = item.Sources is null ? [] : [.. item.Sources] })
                .ToArray() ?? []);
        }

        public string Label
        {
            get => _label;
            set
            {
                if (SetProperty(ref _label, value))
                {
                    OnPropertyChanged(nameof(DisplayName));
                }
            }
        }

        public virtual string RoiTypeName => GetType().Name;

        /// <summary>
        /// 本地化显示名。
        /// Chinese: 由 UI 层通过 <see cref="RoiDisplayNameResolver"/> 安装；无 UI（例如 Core 单测）时回退到类型名。
        /// English: Resolved through <see cref="RoiDisplayNameResolver"/>, which the UI layer installs; falls back to the type name.
        /// </summary>
        public string DisplayTypeName => RoiDisplayNameResolver.GetDisplayName(this);

        public virtual string DisplayName => string.IsNullOrWhiteSpace(Label) ? DisplayTypeName : $"{DisplayTypeName}: {Label}";

        /// <summary>
        /// 复制共享的视觉状态（颜色、粗细、可见性、锁定）。
        /// Chinese: 不复制选中态——选中是瞬时 UI 状态，不属于可快照的视觉状态。
        /// English: Copies the shared visual state. Selection is deliberately excluded: it is transient
        /// UI state, not part of a snapshot.
        /// </summary>
        public void CopyVisualStateFrom(RoiBase source)
        {
            ArgumentNullException.ThrowIfNull(source);

            StrokeColor = source.StrokeColor;
            StrokeThickness = source.StrokeThickness;
            IsVisible = source.IsVisible;
            IsLocked = source.IsLocked;
        }

        /// <summary>Copies persistent identity and advanced measurement definitions when creating a clone.</summary>
        protected void CopyMeasurementIdentityFrom(RoiBase source)
        {
            ArgumentNullException.ThrowIfNull(source);
            MeasurementId = source.MeasurementId;
            AdvancedMeasurements = source.AdvancedMeasurements;
        }

        protected void ApplyCommonState(RoiBase source)
        {
            ArgumentNullException.ThrowIfNull(source);

            Label = source.Label;
            Tolerance = source.Tolerance?.Clone();
            CopyVisualStateFrom(source);
            AdvancedMeasurements = source.AdvancedMeasurements;
        }

        public RoiColor StrokeColor
        {
            get => _strokeColor;
            set => SetProperty(ref _strokeColor, value);
        }

        public double StrokeThickness
        {
            get => _strokeThickness;
            set => SetProperty(ref _strokeThickness, value);
        }

        /// <summary>
        /// 该 ROI 当前是否被选中。
        /// Chinese: 由 ViewModel 维护；仅用于外部读取，绘制选中态由渲染层独立处理。
        /// English: Whether this ROI is currently selected. Maintained by the ViewModel; rendering of the
        /// selection is handled independently by the render layer.
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public bool IsVisible
        {
            get => _isVisible;
            set => SetProperty(ref _isVisible, value);
        }

        public bool IsLocked
        {
            get => _isLocked;
            set => SetProperty(ref _isLocked, value);
        }

        /// <summary>
        /// 测量公差判定参数（标称值 + 上下公差）；非测量 ROI 不使用。
        /// Chinese: 仅在信息面板展示合格/超差判定，不影响绘制与检测。
        /// English: Optional pass-fail tolerance applied to the primary measurement of this ROI.
        /// </summary>
        public MeasurementTolerance? Tolerance
        {
            get => _tolerance;
            set => SetProperty(ref _tolerance, value);
        }

        /// <summary>
        /// 克隆当前 ROI。
        /// Chinese: 返回一个表示当前对象状态的副本，供撤销/重做与状态保存使用；深拷贝或值拷贝依具体实现而定。
        /// English: Creates a copy of the ROI instance for undo/redo or state snapshots. Whether it is a deep or
        /// value copy is up to the concrete implementation.
        /// </summary>
        /// <returns>返回 RoiBase 的副本 / A copy of the RoiBase instance.</returns>
        public abstract RoiBase Clone();

        public abstract void ApplyFrom(RoiBase source);
    }
}
#pragma warning restore CS1591
