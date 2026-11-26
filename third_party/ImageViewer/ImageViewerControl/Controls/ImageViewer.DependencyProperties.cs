using System.Windows;
using System.Windows.Media;
using ImageViewer.Core.Display;
using ImageViewer.Core.Geometry;
using ImageViewer.Models;
using ImageViewer.Services;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        public static readonly DependencyProperty ImageSourceProperty =
            DependencyProperty.Register(nameof(ImageSource), typeof(ImageSource), typeof(ImageViewer),
                new PropertyMetadata(null, OnImageSourceChanged));

        public static readonly DependencyProperty IsImageLoadingProperty =
            DependencyProperty.Register(nameof(IsImageLoading), typeof(bool), typeof(ImageViewer),
                new PropertyMetadata(false));

        public static readonly DependencyProperty ImageLoadProgressProperty =
            DependencyProperty.Register(nameof(ImageLoadProgress), typeof(double), typeof(ImageViewer),
                new PropertyMetadata(0.0));

        public static readonly DependencyProperty ImageLoadStatusTextProperty =
            DependencyProperty.Register(nameof(ImageLoadStatusText), typeof(string), typeof(ImageViewer),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty CanRetryImageLoadProperty =
            DependencyProperty.Register(nameof(CanRetryImageLoad), typeof(bool), typeof(ImageViewer),
                new PropertyMetadata(false));

        public static readonly DependencyProperty ImageLoadHasErrorProperty =
            DependencyProperty.Register(nameof(ImageLoadHasError), typeof(bool), typeof(ImageViewer),
                new PropertyMetadata(false));

        public static readonly DependencyProperty CanCancelImageLoadProperty =
            DependencyProperty.Register(nameof(CanCancelImageLoad), typeof(bool), typeof(ImageViewer),
                new PropertyMetadata(false));

        public static readonly DependencyProperty HasDiagnosticErrorProperty =
            DependencyProperty.Register(nameof(HasDiagnosticError), typeof(bool), typeof(ImageViewer),
                new PropertyMetadata(false));

        public static readonly DependencyProperty IsDirtyProperty =
            DependencyProperty.Register(nameof(IsDirty), typeof(bool), typeof(ImageViewer),
                new PropertyMetadata(false));

        public static readonly DependencyProperty HasRecoverySnapshotProperty =
            DependencyProperty.Register(nameof(HasRecoverySnapshot), typeof(bool), typeof(ImageViewer),
                new PropertyMetadata(false));

        public static readonly DependencyProperty DiagnosticErrorTextProperty =
            DependencyProperty.Register(nameof(DiagnosticErrorText), typeof(string), typeof(ImageViewer),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty ActiveToolNameProperty =
            DependencyProperty.Register(nameof(ActiveToolName), typeof(string), typeof(ImageViewer),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty InteractionModeTextProperty =
            DependencyProperty.Register(nameof(InteractionModeText), typeof(string), typeof(ImageViewer),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty InteractionModeHintProperty =
            DependencyProperty.Register(nameof(InteractionModeHint), typeof(string), typeof(ImageViewer),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty ShowPixelGridProperty =
            DependencyProperty.Register(nameof(ShowPixelGrid), typeof(bool), typeof(ImageViewer),
                new PropertyMetadata(false, OnShowPixelGridChanged));

        public static readonly DependencyProperty ShowCrosshairProperty =
            DependencyProperty.Register(nameof(ShowCrosshair), typeof(bool), typeof(ImageViewer),
                new PropertyMetadata(false, OnShowCrosshairChanged));

        public static readonly DependencyProperty ShowInfoPanelProperty =
            DependencyProperty.Register(nameof(ShowInfoPanel), typeof(bool), typeof(ImageViewer),
                new PropertyMetadata(true, OnShowInfoPanelChanged));

        public static readonly DependencyProperty ShowCaliperScoresProperty =
            DependencyProperty.Register(nameof(ShowCaliperScores), typeof(bool), typeof(ImageViewer),
                new PropertyMetadata(false, OnShowCaliperScoresChanged));

        public static readonly DependencyProperty ShowHistogramProperty =
            DependencyProperty.Register(nameof(ShowHistogram), typeof(bool), typeof(ImageViewer),
                new PropertyMetadata(false, OnShowHistogramChanged));

        public static readonly DependencyProperty ShowProfileProperty =
            DependencyProperty.Register(nameof(ShowProfile), typeof(bool), typeof(ImageViewer),
                new PropertyMetadata(false, OnShowProfileChanged));

        public static readonly DependencyProperty ShowScaleBarProperty =
            DependencyProperty.Register(nameof(ShowScaleBar), typeof(bool), typeof(ImageViewer),
                new PropertyMetadata(false, OnShowScaleBarChanged));

        public static readonly DependencyProperty ShowRoiListProperty =
            DependencyProperty.Register(nameof(ShowRoiList), typeof(bool), typeof(ImageViewer),
                new PropertyMetadata(true, OnShowRoiListChanged));

        public static readonly DependencyProperty ShowToolbarProperty =
            DependencyProperty.Register(nameof(ShowToolbar), typeof(bool), typeof(ImageViewer),
                new PropertyMetadata(true, OnShowToolbarChanged));

        public static readonly DependencyProperty ShowSnapGridProperty =
            DependencyProperty.Register(nameof(ShowSnapGrid), typeof(bool), typeof(ImageViewer),
                new PropertyMetadata(false, OnShowSnapGridChanged));

        public static readonly DependencyProperty EnableSnapToGridProperty =
            DependencyProperty.Register(nameof(EnableSnapToGrid), typeof(bool), typeof(ImageViewer),
                new PropertyMetadata(false));

        public static readonly DependencyProperty GridSpacingProperty =
            DependencyProperty.Register(nameof(GridSpacing), typeof(double), typeof(ImageViewer),
                new PropertyMetadata(10.0, OnShowSnapGridChanged));

        public static readonly DependencyProperty EnableGpuRenderingProperty =
            DependencyProperty.Register(nameof(EnableGpuRendering), typeof(bool), typeof(ImageViewer),
                new PropertyMetadata(false, OnEnableGpuRenderingChanged));

        public static readonly DependencyProperty PseudoColorPaletteProperty =
            DependencyProperty.Register(nameof(PseudoColorPalette), typeof(PseudoColorPalette), typeof(ImageViewer),
                new PropertyMetadata(PseudoColorPalette.None, OnPseudoColorPaletteChanged));

        /// <summary>
        /// 显示调节（窗宽窗位/亮度/对比度/伽马/反色）。
        /// Chinese: 只影响显示，不改像素数据——测量、直方图、导出仍读原始位图。
        /// null 按默认参数（透传）处理，绑定暂时为 null 时不会炸；越界值直接拒绝，免得两条渲染路径各自裁剪出不同结果。
        /// </summary>
        public static readonly DependencyProperty DisplayAdjustmentProperty =
            DependencyProperty.Register(nameof(DisplayAdjustment), typeof(DisplayAdjustment), typeof(ImageViewer),
                new PropertyMetadata(DisplayAdjustment.Identity, OnDisplayAdjustmentChanged), IsValidDisplayAdjustment);

        public static readonly DependencyProperty ScaleProperty =
            DependencyProperty.Register(nameof(Scale), typeof(double), typeof(ImageViewer),
                new PropertyMetadata(1.0, OnScaleChanged));

        public static readonly DependencyProperty PixelSizeProperty =
            DependencyProperty.Register(nameof(PixelSize), typeof(double), typeof(ImageViewer),
                new PropertyMetadata(1.0, OnCalibrationChanged), IsValidPixelSize);

        public static readonly DependencyProperty PhysicalUnitProperty =
            DependencyProperty.Register(nameof(PhysicalUnit), typeof(string), typeof(ImageViewer),
                new PropertyMetadata("px", OnCalibrationChanged));

        public static readonly DependencyProperty CalibrationProperty =
            DependencyProperty.Register(nameof(Calibration), typeof(CameraCalibration), typeof(ImageViewer),
                new PropertyMetadata(null, OnCalibrationChanged));

        /// <summary>
        /// 工件坐标系。
        /// Chinese: 刻意不做成依赖属性——它不是给 XAML 绑定的视觉状态，而是"当前测量方案"的一部分，
        /// 只由配方加载/自动定位设置。放在普通属性上可以避免触发无谓的布局与重绘。
        /// </summary>
        private WorkpieceCoordinateSystem? _workpieceCoordinateSystem;

        public WorkpieceCoordinateSystem? WorkpieceCoordinateSystem
        {
            get => _workpieceCoordinateSystem;
            set
            {
                if (_workpieceCoordinateSystem == value)
                {
                    return;
                }
                _workpieceCoordinateSystem = value;
                _controlComposition?.SessionController.MarkDirty();
            }
        }

        public static readonly DependencyProperty MenuStateProperty =
            DependencyProperty.Register(nameof(MenuState), typeof(object), typeof(ImageViewer),
                new PropertyMetadata(ImageViewerMenuStateSnapshot.Empty));

        public ImageSource? ImageSource
        {
            get => (ImageSource?)GetValue(ImageSourceProperty);
            set => SetValue(ImageSourceProperty, value);
        }

        public bool IsImageLoading
        {
            get => (bool)GetValue(IsImageLoadingProperty);
            set => SetValue(IsImageLoadingProperty, value);
        }

        public double ImageLoadProgress
        {
            get => (double)GetValue(ImageLoadProgressProperty);
            set => SetValue(ImageLoadProgressProperty, value);
        }

        public string ImageLoadStatusText
        {
            get => (string)GetValue(ImageLoadStatusTextProperty);
            set => SetValue(ImageLoadStatusTextProperty, value);
        }

        public bool CanRetryImageLoad
        {
            get => (bool)GetValue(CanRetryImageLoadProperty);
            set => SetValue(CanRetryImageLoadProperty, value);
        }

        public bool ImageLoadHasError
        {
            get => (bool)GetValue(ImageLoadHasErrorProperty);
            set => SetValue(ImageLoadHasErrorProperty, value);
        }

        public bool CanCancelImageLoad
        {
            get => (bool)GetValue(CanCancelImageLoadProperty);
            private set => SetValue(CanCancelImageLoadProperty, value);
        }

        public bool HasDiagnosticError
        {
            get => (bool)GetValue(HasDiagnosticErrorProperty);
            set => SetValue(HasDiagnosticErrorProperty, value);
        }

        public bool IsDirty
        {
            get => (bool)GetValue(IsDirtyProperty);
            private set => SetValue(IsDirtyProperty, value);
        }

        public bool HasRecoverySnapshot
        {
            get => (bool)GetValue(HasRecoverySnapshotProperty);
            private set => SetValue(HasRecoverySnapshotProperty, value);
        }

        public string DiagnosticErrorText
        {
            get => (string)GetValue(DiagnosticErrorTextProperty);
            set => SetValue(DiagnosticErrorTextProperty, value);
        }

        /// <summary>
        /// 当前正在使用的绘制工具名称；没有活动绘制会话时为空。
        /// </summary>
        public string ActiveToolName
        {
            get => (string)GetValue(ActiveToolNameProperty);
            private set => SetValue(ActiveToolNameProperty, value);
        }

        public string InteractionModeText
        {
            get => (string)GetValue(InteractionModeTextProperty);
            private set => SetValue(InteractionModeTextProperty, value);
        }

        public string InteractionModeHint
        {
            get => (string)GetValue(InteractionModeHintProperty);
            private set => SetValue(InteractionModeHintProperty, value);
        }

        public bool ShowPixelGrid
        {
            get => (bool)GetValue(ShowPixelGridProperty);
            set => SetValue(ShowPixelGridProperty, value);
        }

        public bool ShowCrosshair
        {
            get => (bool)GetValue(ShowCrosshairProperty);
            set => SetValue(ShowCrosshairProperty, value);
        }

        public bool ShowInfoPanel
        {
            get => (bool)GetValue(ShowInfoPanelProperty);
            set => SetValue(ShowInfoPanelProperty, value);
        }

        public bool ShowCaliperScores
        {
            get => (bool)GetValue(ShowCaliperScoresProperty);
            set => SetValue(ShowCaliperScoresProperty, value);
        }

        public bool ShowHistogram
        {
            get => (bool)GetValue(ShowHistogramProperty);
            set => SetValue(ShowHistogramProperty, value);
        }

        public bool ShowProfile
        {
            get => (bool)GetValue(ShowProfileProperty);
            set => SetValue(ShowProfileProperty, value);
        }

        public bool ShowScaleBar
        {
            get => (bool)GetValue(ShowScaleBarProperty);
            set => SetValue(ShowScaleBarProperty, value);
        }

        public bool ShowRoiList
        {
            get => (bool)GetValue(ShowRoiListProperty);
            set => SetValue(ShowRoiListProperty, value);
        }

        public bool ShowToolbar
        {
            get => (bool)GetValue(ShowToolbarProperty);
            set => SetValue(ShowToolbarProperty, value);
        }

        public bool ShowSnapGrid
        {
            get => (bool)GetValue(ShowSnapGridProperty);
            set => SetValue(ShowSnapGridProperty, value);
        }

        public bool EnableSnapToGrid
        {
            get => (bool)GetValue(EnableSnapToGridProperty);
            set => SetValue(EnableSnapToGridProperty, value);
        }

        public double GridSpacing
        {
            get => (double)GetValue(GridSpacingProperty);
            set => SetValue(GridSpacingProperty, value);
        }

        public bool EnableGpuRendering
        {
            get => (bool)GetValue(EnableGpuRenderingProperty);
            set => SetValue(EnableGpuRenderingProperty, value);
        }

        public PseudoColorPalette PseudoColorPalette
        {
            get => (PseudoColorPalette)GetValue(PseudoColorPaletteProperty);
            set => SetValue(PseudoColorPaletteProperty, value);
        }

        public DisplayAdjustment DisplayAdjustment
        {
            get => (DisplayAdjustment?)GetValue(DisplayAdjustmentProperty) ?? DisplayAdjustment.Identity;
            set => SetValue(DisplayAdjustmentProperty, value);
        }

        public double Scale
        {
            get => (double)GetValue(ScaleProperty);
            set => SetValue(ScaleProperty, value);
        }

        public double PixelSize
        {
            get => (double)GetValue(PixelSizeProperty);
            set => SetValue(PixelSizeProperty, value);
        }

        public string PhysicalUnit
        {
            get => (string)GetValue(PhysicalUnitProperty);
            set => SetValue(PhysicalUnitProperty, value);
        }

        public CameraCalibration? Calibration
        {
            get => (CameraCalibration?)GetValue(CalibrationProperty);
            set => SetValue(CalibrationProperty, value);
        }

        public object MenuState
        {
            get => GetValue(MenuStateProperty);
            private set => SetValue(MenuStateProperty, value);
        }

        private static void WithViewer(DependencyObject dependencyObject, Action<ImageViewer> callback)
        {
            if (dependencyObject is ImageViewer viewer)
            {
                callback(viewer);
            }
        }

        private static void WithViewer<TValue>(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e, Action<ImageViewer, TValue> callback)
        {
            if (dependencyObject is ImageViewer viewer && e.NewValue is TValue value)
            {
                callback(viewer, value);
            }
        }

        private static void OnShowRoiListChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            WithViewer<bool>(d, e, (viewer, value) => viewer._imageViewStateController.HandleShowRoiListChanged(value));
        }

        private static void OnShowToolbarChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            WithViewer<bool>(d, e, (viewer, isVisible) =>
            {
                viewer.UpdateToolbarLayout();
                viewer.UpdateContextMenuState();
            });
        }

        private static bool IsValidPixelSize(object value)
        {
            return value is double pixelSize
                && double.IsFinite(pixelSize)
                && pixelSize > 0;
        }

        private static bool IsValidDisplayAdjustment(object value)
        {
            // null 允许：显示设置构造时按默认参数（透传）处理，绑定暂时取到 null 不应该让 SetValue 抛异常。
            return value is not DisplayAdjustment adjustment || adjustment.TryValidate(out _);
        }

        private static void OnImageSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            WithViewer(d, viewer =>
            {
                viewer.ClearAllRecipeDatums();
                viewer._imageSourceController.HandleImageSourceChanged(e.NewValue as ImageSource);
                viewer.UpdateStatusBar();
                // Keep the visible toolbar and context menu in sync as soon as a new image is assigned.
                // Without this refresh, commands such as Save, Undo and Export can remain disabled
                // until the user opens the context menu or invokes another command first.
                viewer.UpdateContextMenuState();
                if (viewer._isChangingVolumeSlice)
                {
                    viewer._sessionController.RecordVolumeSliceNavigation();
                }
                else if (viewer.IsLoaded)
                {
                    viewer.MarkDocumentDirty();
                }
            });
        }

        private static void OnCalibrationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            WithViewer(d, viewer =>
            {
                viewer.UpdateCalibrationIndicator();
                viewer._imageViewStateController.HandleCalibrationChanged();
                if (viewer.IsLoaded)
                {
                    viewer.MarkDocumentDirty();
                }
            });
        }

        private static void OnShowPixelGridChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            WithViewer(d, viewer => viewer._imageViewStateController.HandleShowPixelGridChanged());
        }

        private static void OnShowSnapGridChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            WithViewer(d, viewer => viewer._imageViewStateController.HandleShowSnapGridChanged());
        }

        private static void OnShowCrosshairChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            WithViewer<bool>(d, e, (viewer, value) => viewer._imageViewStateController.HandleShowCrosshairChanged(value));
        }

        private static void OnShowInfoPanelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            WithViewer<bool>(d, e, (viewer, value) => viewer._imageViewStateController.HandleShowInfoPanelChanged(value));
        }

        private static void OnShowCaliperScoresChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            WithViewer(d, viewer => viewer.DrawRois());
        }

        private static void OnShowScaleBarChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            WithViewer<bool>(d, e, (viewer, value) => viewer._imageViewStateController.HandleShowScaleBarChanged(value));
        }

        private static void OnScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            WithViewer<double>(d, e, (viewer, value) => viewer._imageViewStateController.HandleScaleChanged(value));
        }
    }
}
