using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using ImageViewer.Core.Measurements;
using ImageViewer.Localization;

namespace ImageViewer.Controls
{
    internal sealed record MenuSearchCommand(MenuItem Original, string Header, string Path, string[] Aliases, bool IsEnabled, string? DisabledReason)
    {
    }

    /// <summary>Builds stable flattened command entries for the context-menu search view.</summary>
    internal static class MenuSearchCommands
    {
        private static readonly (string[] Keys, string[] Aliases)[] Synonyms =
        [
            (["circle", "圆", "圆形", "圆测量"], ["孔径", "直径", "diameter", "radius", "半径"]),
            (["caliper", "卡尺", "卡尺测量"], ["缝宽", "宽度", "gap", "thickness", "间隙"]),
            (["line", "直线度", "line profile"], ["长度", "距离", "length", "distance"]),
            (["profile", "直线度"], ["形状误差", "straightness"]),
        ];

        public static IReadOnlyList<MenuSearchCommand> Find(IEnumerable<object> items, string[] words)
        {
            return Find(items, words, menuItem => menuItem.Visibility != System.Windows.Visibility.Collapsed);
        }

        public static IReadOnlyList<MenuSearchCommand> Find(IEnumerable<object> items, string[] words, Func<MenuItem, bool> isAvailable)
        {
            List<MenuSearchCommand> found = [];
            foreach (object item in items)
            {
                if (item is MenuItem menuItem && menuItem.Tag is not (MenuSearchResultTag or MenuSearchHostTag))
                {
                    Visit(menuItem, [], words, found, isAvailable);
                }
            }

            return found;
        }

        public static bool Matches(string text, string[] words)
        {
            string normalized = Normalize(text);
            return words.All(word => normalized.Contains(Normalize(word), StringComparison.Ordinal));
        }

        public static string Normalize(string text)
        {
            Span<char> buffer = text.Length <= 256 ? stackalloc char[text.Length] : new char[text.Length];
            int count = 0;
            foreach (char character in text)
            {
                if (!char.IsWhiteSpace(character) && character is not ('+' or '-' or '.' or '/' or '\\' or '(' or ')' or '（' or '）'))
                {
                    buffer[count++] = char.ToLowerInvariant(character);
                }
            }

            return new string(buffer[..count]);
        }

        private static void Visit(MenuItem item, List<string> parents, string[] words, List<MenuSearchCommand> found, Func<MenuItem, bool> isAvailable)
        {
            if (!isAvailable(item))
            {
                return;
            }

            List<string> pathParts = [.. parents];
            string header = item.Header?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(header))
            {
                pathParts.Add(header);
            }

            if (item.Items.Count > 0)
            {
                foreach (object child in item.Items)
                {
                    if (child is MenuItem nested && nested.Tag is not (MenuSearchResultTag or MenuSearchHostTag))
                    {
                        Visit(nested, pathParts, words, found, isAvailable);
                    }
                }

                return;
            }

            string path = string.Join(" › ", pathParts);
            string[] aliases = GetAliases(item, path);
            string searchable = string.Join(" ", new[] { header, path, item.InputGestureText, item.ToolTip?.ToString() }.Concat(aliases));
            if (!Matches(searchable, words))
            {
                return;
            }

            string? disabledReason = item.IsEnabled ? null : item.ToolTip?.ToString();
            found.Add(new MenuSearchCommand(item, header, path, aliases, item.IsEnabled, disabledReason));
        }

        private static string[] GetAliases(MenuItem item, string path)
        {
            List<string> aliases = [];
            if (item.Tag is ImageViewerDetectionRecommendationMenuTag)
                aliases.Add(UiText.Get("DetectionRecommendationAliases"));
            string text = $"{item.Header} {item.InputGestureText} {item.ToolTip}";
            string normalized = Normalize(text);
            foreach ((string[] keys, string[] values) in Synonyms)
            {
                if (keys.Any(key => normalized.Contains(Normalize(key), StringComparison.Ordinal)))
                {
                    aliases.AddRange(values);
                }
            }

            if (ReferenceEquals(item.Tag, ImageViewerRoiMenuTags.Undo))
            {
                aliases.AddRange(["undo", "撤销"]);
            }
            else if (ReferenceEquals(item.Tag, ImageViewerRoiMenuTags.Redo))
            {
                aliases.AddRange(["redo", "重做"]);
            }

            if (ReferenceEquals(item.Tag, ImageViewerViewMenuTags.FitToView))
            {
                aliases.AddRange(["fit", "适合视图", "适配窗口"]);
            }

            if (item.Tag is ImageViewerAdvancedMeasurementMenuTag advanced)
            {
                aliases.Add(advanced.Kind.ToString());
                aliases.Add(UiText.Get(advanced.Descriptor.AliasesKey));
                if (advanced.Kind == AdvancedMeasurementKind.Circularity)
                {
                    aliases.AddRange(["roundness", "圆度", "circularity"]);
                }
            }

            return aliases.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

    }

    internal sealed class MenuSearchResultTag(MenuItem original)
    {
        public MenuItem Original { get; } = original;
    }

    internal sealed class MenuSearchHostTag
    {
    }
}
