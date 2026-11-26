using System;
using System.Windows.Markup;

namespace ImageViewerDemo.Localization;

[MarkupExtensionReturnType(typeof(string))]
public sealed class DemoTextExtension : MarkupExtension
{
    public DemoTextExtension()
    {
    }

    public DemoTextExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return DemoText.Get(Key);
    }
}