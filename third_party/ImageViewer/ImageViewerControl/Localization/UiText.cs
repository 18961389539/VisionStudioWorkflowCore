using System;
using System.Globalization;
using System.Resources;

namespace ImageViewer.Localization
{
    public static class UiText
    {
        private static readonly ResourceManager ResourceManager = new("ImageViewerControl.Resources.UiText", typeof(UiText).Assembly);

        public static string Get(string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);

            return ResourceManager.GetString(key, CultureInfo.CurrentUICulture)
                ?? throw new InvalidOperationException($"Missing UI text resource '{key}'.");
        }

        public static string Format(string key, params object?[] args)
        {
            return string.Format(CultureInfo.CurrentCulture, Get(key), args);
        }

        public static string FormatInvariant(string key, params object?[] args)
        {
            return string.Format(CultureInfo.InvariantCulture, Get(key), args);
        }
    }
}