#pragma warning disable CS1591
using System;
using System.Collections.Generic;

namespace ImageViewer.Models
{
    /// <summary>
    /// 内置 ROI 使用的命名颜色。
    /// Chinese: 取值与 WPF Colors 中同名颜色完全一致，由测试逐一校验。
    /// English: Named colors used by the built-in ROIs. Values match the WPF Colors entries of the same
    /// name; a test verifies each one.
    /// </summary>
    public static class RoiColors
    {
        public static RoiColor Black { get; } = RoiColor.FromArgb(255, 0, 0, 0);

        public static RoiColor White { get; } = RoiColor.FromArgb(255, 255, 255, 255);

        public static RoiColor Cyan { get; } = RoiColor.FromArgb(255, 0, 255, 255);

        public static RoiColor Red { get; } = RoiColor.FromArgb(255, 255, 0, 0);

        public static RoiColor Green { get; } = RoiColor.FromArgb(255, 0, 128, 0);

        public static RoiColor Yellow { get; } = RoiColor.FromArgb(255, 255, 255, 0);

        public static RoiColor Magenta { get; } = RoiColor.FromArgb(255, 255, 0, 255);

        public static RoiColor Gold { get; } = RoiColor.FromArgb(255, 255, 215, 0);

        public static RoiColor Lime { get; } = RoiColor.FromArgb(255, 0, 255, 0);

        public static RoiColor LimeGreen { get; } = RoiColor.FromArgb(255, 50, 205, 50);

        public static RoiColor Coral { get; } = RoiColor.FromArgb(255, 255, 127, 80);

        public static RoiColor Orchid { get; } = RoiColor.FromArgb(255, 218, 112, 214);

        public static RoiColor Pink { get; } = RoiColor.FromArgb(255, 255, 192, 203);

        public static RoiColor Turquoise { get; } = RoiColor.FromArgb(255, 64, 224, 208);

        public static RoiColor DeepSkyBlue { get; } = RoiColor.FromArgb(255, 0, 191, 255);

        public static RoiColor LightGreen { get; } = RoiColor.FromArgb(255, 144, 238, 144);

        public static RoiColor Orange { get; } = RoiColor.FromArgb(255, 255, 165, 0);

        /// <summary>
        /// 按名称解析命名色，不区分大小写。
        /// Chinese: 供 RoiColor.Parse 处理既有会话文件里的命名色写法。
        /// English: Resolves a named color, case-insensitively. Used by RoiColor.Parse to handle named
        /// colors that may appear in existing session files.
        /// </summary>
        public static bool TryParseNamed(string name, out RoiColor color)
        {
            color = default;

            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            if (NamedColors.TryGetValue(name.Trim(), out RoiColor found))
            {
                color = found;
                return true;
            }

            return false;
        }

        private static readonly Dictionary<string, RoiColor> NamedColors = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Black"] = Black,
            ["White"] = White,
            ["Cyan"] = Cyan,
            ["Red"] = Red,
            ["Green"] = Green,
            ["Yellow"] = Yellow,
            ["Magenta"] = Magenta,
            ["Gold"] = Gold,
            ["Lime"] = Lime,
            ["LimeGreen"] = LimeGreen,
            ["Coral"] = Coral,
            ["Orchid"] = Orchid,
            ["Pink"] = Pink,
            ["Turquoise"] = Turquoise,
            ["DeepSkyBlue"] = DeepSkyBlue,
            ["LightGreen"] = LightGreen,
            ["Orange"] = Orange
        };
    }
}
#pragma warning restore CS1591
