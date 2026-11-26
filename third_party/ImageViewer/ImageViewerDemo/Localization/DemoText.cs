using System;
using System.Globalization;
using System.Resources;

namespace ImageViewerDemo.Localization;

public static class DemoText
{
    private static readonly ResourceManager ResourceManager = new("ImageViewerDemo.Resources.DemoText", typeof(DemoText).Assembly);

    public static string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return ResourceManager.GetString(key, CultureInfo.CurrentUICulture)
            ?? throw new InvalidOperationException($"Missing demo UI text resource '{key}'.");
    }

    public static string Format(string key, params object?[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, Get(key), args);
    }
}