using System;
using System.Windows.Markup;

namespace ImageViewer.Localization
{
    [MarkupExtensionReturnType(typeof(string))]
    public sealed class UiTextExtension : MarkupExtension
    {
        public UiTextExtension()
        {
        }

        public UiTextExtension(string key)
        {
            Key = key;
        }

        [ConstructorArgument("key")]
        public string Key { get; set; } = string.Empty;

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            return UiText.Get(Key);
        }
    }
}