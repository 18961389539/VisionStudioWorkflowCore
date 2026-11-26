using System;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ImageViewer.Localization;

namespace ImageViewer.Controls
{
    internal enum StatusHintKind
    {
        Info,
        Success,
        Error
    }

    public partial class ImageViewer
    {
        private DispatcherTimer? _statusHintTimer;
        private DispatcherTimer? _zoomBadgeTimer;
        private const double ZoomBadgeOffset = 10;
        private const double ZoomBadgeGapAboveCursor = 12;

        /// <summary>
        /// 显示一条临时状态提示（顶部居中横幅），约 durationMs 后自动淡出消失。
        /// 连续调用会重置计时而不排队，适合缩放比例等高频反馈。
        /// </summary>
        internal void ShowStatusHint(string message, StatusHintKind kind = StatusHintKind.Info, int durationMs = 3000)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => ShowStatusHint(message, kind, durationMs));
                return;
            }

            ApplyStatusHintStyle(kind);
            statusHintTextBlock.Text = message;
            statusHintBorder.Visibility = Visibility.Visible;
            RaiseLiveRegionChanged(statusHintTextBlock);

            // 轻快淡入，与淡出对称
            statusHintBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(120)));

            if (_statusHintTimer == null)
            {
                _statusHintTimer = new DispatcherTimer();
                _statusHintTimer.Tick += OnStatusHintTimerTick;
            }

            _statusHintTimer.Stop();
            _statusHintTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(200, durationMs));
            _statusHintTimer.Start();
        }

        /// <summary>立即隐藏状态提示，测试或主动关闭时使用。</summary>
        internal void DismissStatusHint()
        {
            statusHintBorder.BeginAnimation(OpacityProperty, null);
            statusHintBorder.Opacity = 1;
            statusHintBorder.Visibility = Visibility.Collapsed;
        }

        private void OnStatusHintTimerTick(object? sender, EventArgs e)
        {
            _statusHintTimer?.Stop();
            FadeOutStatusHint();
        }

        private void FadeOutStatusHint()
        {
            if (statusHintBorder.Visibility != Visibility.Visible)
            {
                return;
            }

            var fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(200));
            fade.Completed += (_, _) => statusHintBorder.Visibility = Visibility.Collapsed;
            statusHintBorder.BeginAnimation(OpacityProperty, fade);
        }

        private void ApplyStatusHintStyle(StatusHintKind kind)
        {
            switch (kind)
            {
                case StatusHintKind.Success:
                    statusHintBorder.Background = GetBrushResource("ViewerAccentSoftBrush");
                    statusHintBorder.BorderBrush = GetBrushResource("ViewerAccentBorderBrush");
                    statusHintTextBlock.Foreground = GetBrushResource("ViewerTextPrimaryBrush");
                    ApplyStatusHintIcon("StatusHintIconSuccess", GetBrushResource("ViewerAccentBrush"));
                    break;
                case StatusHintKind.Error:
                    statusHintBorder.Background = GetBrushResource("ViewerErrorSoftBrush");
                    statusHintBorder.BorderBrush = GetBrushResource("ViewerErrorBorderBrush");
                    statusHintTextBlock.Foreground = GetBrushResource("ViewerTextPrimaryBrush");
                    ApplyStatusHintIcon("StatusHintIconError", GetBrushResource("ViewerErrorBrush"));
                    break;
                default:
                    statusHintBorder.Background = GetBrushResource("ViewerSurfaceBrush");
                    statusHintBorder.BorderBrush = GetBrushResource("ViewerPanelBorderBrush");
                    statusHintTextBlock.Foreground = GetBrushResource("ViewerTextPrimaryBrush");
                    ApplyStatusHintIcon(null, null);
                    break;
            }
        }

        /// <summary>
        /// 用图标前缀区分成功/错误提示，避免仅靠边框颜色传达结果（对色觉障碍用户更友好）。
        /// Chinese: 信息类提示不显示图标；成功与错误提示显示对应符号并着色。
        /// English: Shows a symbolic icon for success/error hints so status is not conveyed by color alone.
        /// </summary>
        private void ApplyStatusHintIcon(string? iconResourceKey, Brush? foreground)
        {
            if (statusHintIconTextBlock is null)
            {
                return;
            }

            if (string.IsNullOrEmpty(iconResourceKey))
            {
                statusHintIconTextBlock.Visibility = Visibility.Collapsed;
                return;
            }

            statusHintIconTextBlock.Text = UiText.Get(iconResourceKey);
            statusHintIconTextBlock.Foreground = foreground;
            statusHintIconTextBlock.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// 显式抛出 LiveRegionChanged，使已声明的 AutomationProperties.LiveSetting 真正通知屏幕阅读器。
        /// Chinese: WPF 中 LiveSetting 仅是元数据，必须在文本变化时主动抛出事件才会被朗读。
        /// English: Raises LiveRegionChanged so assistive technology actually announces the updated text.
        /// </summary>
        private static void RaiseLiveRegionChanged(UIElement element)
        {
            AutomationPeer? peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        private Brush GetBrushResource(string key)
        {
            return (Brush)(TryFindResource(key) ?? new SolidColorBrush(Colors.Gray));
        }

        /// <summary>
        /// 在光标附近短暂显示缩放比例徽标，约 0.9 秒后淡出。
        /// 滚轮缩放与 +/- /0 快捷键调用；连续调用重置计时不排队。
        /// </summary>
        internal void ShowZoomBadge(string message, Point rootPosition)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => ShowZoomBadge(message, rootPosition));
                return;
            }

            zoomBadgeTextBlock.Text = message;
            zoomBadgeBorder.Visibility = Visibility.Visible;
            zoomBadgeBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double badgeWidth = zoomBadgeBorder.DesiredSize.Width;
            double badgeHeight = zoomBadgeBorder.DesiredSize.Height;

            // 显示在光标上方并略右移，避免遮挡光标处的观察内容；在视口内夹取防止裁剪
            double left = Math.Min(Math.Max(0, rootPosition.X + ZoomBadgeOffset), Math.Max(0, rootGrid.ActualWidth - badgeWidth - 2));
            double preferredTop = rootPosition.Y - badgeHeight - ZoomBadgeGapAboveCursor;
            double top = preferredTop < 0
                ? Math.Min(Math.Max(0, rootPosition.Y + ZoomBadgeGapAboveCursor), Math.Max(0, rootGrid.ActualHeight - badgeHeight - 2))
                : preferredTop;
            Canvas.SetLeft(zoomBadgeBorder, left);
            Canvas.SetTop(zoomBadgeBorder, top);

            // 快速淡入
            zoomBadgeBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(120)));

            if (_zoomBadgeTimer == null)
            {
                _zoomBadgeTimer = new DispatcherTimer();
                _zoomBadgeTimer.Tick += OnZoomBadgeTimerTick;
            }

            _zoomBadgeTimer.Stop();
            _zoomBadgeTimer.Interval = TimeSpan.FromMilliseconds(900);
            _zoomBadgeTimer.Start();
        }

        private void OnZoomBadgeTimerTick(object? sender, EventArgs e)
        {
            _zoomBadgeTimer?.Stop();
            FadeOutZoomBadge();
        }

        private void FadeOutZoomBadge()
        {
            if (zoomBadgeBorder.Visibility != Visibility.Visible)
            {
                return;
            }

            var fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(180));
            fade.Completed += (_, _) => zoomBadgeBorder.Visibility = Visibility.Collapsed;
            zoomBadgeBorder.BeginAnimation(OpacityProperty, fade);
        }

        internal void DismissZoomBadge()
        {
            zoomBadgeBorder.BeginAnimation(OpacityProperty, null);
            zoomBadgeBorder.Visibility = Visibility.Collapsed;
        }
    }
}
