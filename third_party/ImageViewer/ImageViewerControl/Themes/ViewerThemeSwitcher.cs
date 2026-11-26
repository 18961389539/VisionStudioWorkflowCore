using System;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace ImageViewer.Controls
{
    /// <summary>
    /// 控件主题切换助手。
    /// Chinese: 控件在 XAML 里默认合并了内置深色主题（保证控件自给自足，测试/设计器无宿主也能渲染）。
    /// 宿主想要换肤时，调用 <see cref="Apply"/> 把这套内置字典**就地替换**为指定主题，或将整套字典替换为自定义字典。
    /// 由于控件内部的颜色都通过 <c>DynamicResource</c> 引用，替换后即时生效。
    /// English: Swaps the control-local built-in theme dictionary in place. Every color inside the control is
    /// referenced through DynamicResource, so the swap takes effect immediately.
    /// </summary>
    public static class ViewerThemeSwitcher
    {
        /// <summary>
        /// 记录"当前由本助手安装的字典"。
        /// Chinese: 仅靠 <c>Source</c> 无法识别自定义字典（宿主传入的字典通常没有 Source），
        /// 若不记录就会在第二次替换时把新字典插到索引 0，而旧自定义字典优先级更高、继续生效。
        /// 用 <see cref="ConditionalWeakTable{TKey,TValue}"/> 挂到元素上，避免静态引用阻止控件回收。
        /// English: Tracks the dictionary this helper last installed on each element. Without it, a second
        /// replacement would insert the new dictionary at index 0 while the previous custom dictionary keeps
        /// shadowing it. Attached via a weak table so it never roots the element.
        /// </summary>
        private static readonly ConditionalWeakTable<FrameworkElement, ThemeSlot> ThemeSlots = new();

        /// <summary>
        /// 把控件资源里的内置主题字典替换成指定内置主题。
        /// English: Replaces the built-in theme dictionary with another built-in theme.
        /// </summary>
        public static void Apply(FrameworkElement element, ViewerThemes.Theme theme)
        {
            Apply(element, ViewerThemes.CreateDictionary(theme));
        }

        /// <summary>
        /// 把控件资源里的内置主题字典替换成宿主提供的自定义字典。
        /// Chinese: 自定义字典需与内置主题使用相同的键名（见 Themes/ViewerTheme.Dark.xaml 的 x:Key 列表）。
        /// English: The custom dictionary must use the same key names as the built-in themes.
        /// </summary>
        public static void Apply(FrameworkElement element, ResourceDictionary themeDictionary)
        {
            ArgumentNullException.ThrowIfNull(element);
            ArgumentNullException.ThrowIfNull(themeDictionary);

            Collection<ResourceDictionary> merged = element.Resources.MergedDictionaries;
            ThemeSlot slot = ThemeSlots.GetOrCreateValue(element);

            if (TryReplaceInPlace(merged, themeDictionary))
            {
                slot.Current = themeDictionary;
                return;
            }

            if (slot.Current is not null)
            {
                // 之前装过（很可能是自定义字典，没有 Source 所以上面识别不到）：先摘掉旧的再装新的，
                // 否则旧字典仍在更靠前的索引上，新字典永远轮不到它生效。
                merged.Remove(slot.Current);
            }

            merged.Insert(0, themeDictionary);
            slot.Current = themeDictionary;
        }

        /// <summary>
        /// 卸载由 <see cref="Apply(FrameworkElement, ResourceDictionary)"/> 安装的主题字典，回到控件 XAML 自带的默认主题。
        /// Chinese: 传 <c>null</c> 给 ThemeDictionary 属性并不表示"卸载"，只表示"改用 Theme 属性"，
        /// 需要真正恢复默认外观时调用本方法。
        /// English: Removes the dictionary installed by Apply and falls back to the control's built-in default.
        /// </summary>
        public static void Clear(FrameworkElement element)
        {
            ArgumentNullException.ThrowIfNull(element);

            if (!ThemeSlots.TryGetValue(element, out ThemeSlot? slot) || slot.Current is null)
            {
                return;
            }

            element.Resources.MergedDictionaries.Remove(slot.Current);
            slot.Current = null;
        }

        /// <summary>
        /// 就地替换内置主题字典；返回 false 表示当前没有内置主题字典、调用方需自行插入。
        /// </summary>
        public static bool TryReplaceInPlace(Collection<ResourceDictionary> merged, ResourceDictionary themeDictionary)
        {
            ArgumentNullException.ThrowIfNull(merged);
            ArgumentNullException.ThrowIfNull(themeDictionary);

            for (int index = 0; index < merged.Count; index++)
            {
                ResourceDictionary candidate = merged[index];
                if (candidate.Source is not null && ViewerThemes.IsBuiltInThemeSource(candidate.Source))
                {
                    merged[index] = themeDictionary;
                    return true;
                }
            }

            return false;
        }

        private sealed class ThemeSlot
        {
            public ResourceDictionary? Current { get; set; }
        }
    }
}
