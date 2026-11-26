using System.Windows;
using System.Windows.Controls;

namespace VisionStudio.Wpf.Themes;

/// <summary>
/// 提供轻量级附加属性（例如 TextBox 占位提示文本），避免为简单提示引入额外控件库依赖。
/// </summary>
public static class TextBoxHelper
{
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder",
        typeof(string),
        typeof(TextBoxHelper),
        new PropertyMetadata(string.Empty));

    public static string GetPlaceholder(DependencyObject obj) => (string)obj.GetValue(PlaceholderProperty);

    public static void SetPlaceholder(DependencyObject obj, string value) => obj.SetValue(PlaceholderProperty, value);
}
