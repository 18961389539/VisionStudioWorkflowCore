using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using VisionStudio.Abstractions;

namespace VisionStudio.Wpf.Converters;

/// <summary>字符串或对象为空时返回 Collapsed（可选反转）。</summary>
public sealed class NullOrEmptyToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isEmpty = value switch
        {
            null => true,
            string s => string.IsNullOrWhiteSpace(s),
            _ => false
        };
        if (Invert) isEmpty = !isEmpty;
        return isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>布尔值反转转 Visibility。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>集合/整数数量为 0 时显示或隐藏。</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public bool ShowWhenZero { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value is int i ? i : 0;
        var visible = ShowWhenZero ? count == 0 : count > 0;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>将运行状态、告警级别、资产健康或判定结果映射为工业暗色徽标画刷。</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public string Role { get; set; } = "Background";

    private static readonly SolidColorBrush SuccessBg = Freeze(new SolidColorBrush(Color.FromRgb(0x14, 0x3D, 0x2B)));
    private static readonly SolidColorBrush SuccessFg = Freeze(new SolidColorBrush(Color.FromRgb(0x86, 0xEF, 0xAC)));
    private static readonly SolidColorBrush DangerBg = Freeze(new SolidColorBrush(Color.FromRgb(0x45, 0x1A, 0x1E)));
    private static readonly SolidColorBrush DangerFg = Freeze(new SolidColorBrush(Color.FromRgb(0xFC, 0xA5, 0xA5)));
    private static readonly SolidColorBrush WarningBg = Freeze(new SolidColorBrush(Color.FromRgb(0x42, 0x2B, 0x0F)));
    private static readonly SolidColorBrush WarningFg = Freeze(new SolidColorBrush(Color.FromRgb(0xFC, 0xD3, 0x4D)));
    private static readonly SolidColorBrush InfoBg = Freeze(new SolidColorBrush(Color.FromRgb(0x17, 0x2E, 0x4F)));
    private static readonly SolidColorBrush InfoFg = Freeze(new SolidColorBrush(Color.FromRgb(0x93, 0xC5, 0xFD)));
    private static readonly SolidColorBrush NeutralBg = Freeze(new SolidColorBrush(Color.FromRgb(0x23, 0x2C, 0x3B)));
    private static readonly SolidColorBrush NeutralFg = Freeze(new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value?.ToString()?.Trim() ?? string.Empty;
        var (bg, fg) = Classify(text);
        return Role.Equals("Foreground", StringComparison.OrdinalIgnoreCase) ? fg : bg;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    private static (SolidColorBrush Bg, SolidColorBrush Fg) Classify(string text)
    {
        if (string.IsNullOrEmpty(text)) return (NeutralBg, NeutralFg);
        var upper = text.ToUpperInvariant();

        if (upper is "RUNNING" or "HEALTHY" or "OK" or "SUCCEEDED" or "TRUE"
            || text.Contains("正常", StringComparison.Ordinal)
            || text.Contains("成功", StringComparison.Ordinal)
            || text.Contains("采集中", StringComparison.Ordinal)
            || text.Contains("在线", StringComparison.Ordinal)
            || text.Contains("已确认", StringComparison.Ordinal))
        {
            return (SuccessBg, SuccessFg);
        }

        if (upper is "FAULTED" or "ERROR" or "CRITICAL" or "FAILED" or "NG" or "FALSE"
            || text.Contains("故障", StringComparison.Ordinal)
            || text.Contains("失败", StringComparison.Ordinal)
            || text.Contains("错误", StringComparison.Ordinal)
            || text.Contains("严重", StringComparison.Ordinal))
        {
            return (DangerBg, DangerFg);
        }

        if (upper is "WARNING" or "WARN" or "DEGRADED" or "STARTING" or "STOPPING" or "PAUSED"
            || text.Contains("降级", StringComparison.Ordinal)
            || text.Contains("警告", StringComparison.Ordinal)
            || text.Contains("未确认", StringComparison.Ordinal)
            || text.Contains("已打开", StringComparison.Ordinal))
        {
            return (WarningBg, WarningFg);
        }

        if (upper is "INFO" or "INFORMATION" or "DEBUG"
            || text.Contains("信息", StringComparison.Ordinal)
            || text.Contains("运行中", StringComparison.Ordinal))
        {
            return (InfoBg, InfoFg);
        }

        return (NeutralBg, NeutralFg);
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}

/// <summary>按端口数据类型映射标识颜色。</summary>
public sealed class DataTypeToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush ControlBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));
    private static readonly SolidColorBrush ImageBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)));
    private static readonly SolidColorBrush RegionBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA)));
    private static readonly SolidColorBrush ValueBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)));
    private static readonly SolidColorBrush GeometryBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF4, 0x72, 0xB6)));
    private static readonly SolidColorBrush AnyBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not VisionDataType dataType) return AnyBrush;
        return dataType switch
        {
            VisionDataType.Control => ControlBrush,
            VisionDataType.Image or VisionDataType.DomainImage or VisionDataType.FrameSet => ImageBrush,
            VisionDataType.Region or VisionDataType.Roi => RegionBrush,
            VisionDataType.Point2D or VisionDataType.Line2D or VisionDataType.Circle
                or VisionDataType.Rectangle2D or VisionDataType.Pose2D
                or VisionDataType.Contour or VisionDataType.Transform2D
                or VisionDataType.CoordinatePoint2D or VisionDataType.CoordinateLine2D
                or VisionDataType.CoordinatePose2D => GeometryBrush,
            VisionDataType.Any => AnyBrush,
            _ => ValueBrush
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
