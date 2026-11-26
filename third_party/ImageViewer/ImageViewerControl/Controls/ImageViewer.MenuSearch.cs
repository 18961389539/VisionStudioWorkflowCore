using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Plugins;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        /// <summary>
        /// 菜单搜索区的模板部件缓存。搜索框位于 <see cref="ContextMenu"/> 的 ControlTemplate 顶部，
        /// 因此需通过模板查找获取（不再是 XAML 生成的字段），以保证它横贯菜单宽度而不被项容器左对齐压扁。
        /// </summary>
        private TextBox? _menuSearchBox;
        private TextBlock? _menuSearchMatchCountText;
        private TextBlock? _menuSearchNoResultsText;
        private MenuItem? _menuSearchResultsHost;
        private readonly List<MenuItem> _menuSearchResultItems = new();
        private readonly Dictionary<UIElement, MenuSearchVisibilitySnapshot> _menuSearchVisibilitySnapshots = new();

        private sealed record MenuSearchVisibilitySnapshot(BindingBase? Binding, Visibility Value);

        private void EnsureMenuSearchTemplateParts()
        {
            if (_menuSearchBox is not null)
            {
                return;
            }

            mainContextMenu.ApplyTemplate();

            _menuSearchBox = mainContextMenu.Template?.FindName("PART_MenuSearchBox", mainContextMenu) as TextBox
                ?? throw new InvalidOperationException("菜单搜索区模板部件 'PART_MenuSearchBox' 缺失。");
            _menuSearchMatchCountText = mainContextMenu.Template?.FindName("PART_MenuSearchMatchCount", mainContextMenu) as TextBlock
                ?? throw new InvalidOperationException("菜单搜索区模板部件 'PART_MenuSearchMatchCount' 缺失。");
            _menuSearchNoResultsText = mainContextMenu.Template?.FindName("PART_MenuSearchNoResults", mainContextMenu) as TextBlock
                ?? throw new InvalidOperationException("菜单搜索区模板部件 'PART_MenuSearchNoResults' 缺失。");
            EnsureMenuSearchResultsHost();
        }

        private void EnsureMenuSearchResultsHost()
        {
            if (_menuSearchResultsHost is not null)
            {
                return;
            }

            _menuSearchResultsHost = new MenuItem
            {
                Header = UiText.Get("MenuSearchResults"),
                Tag = new MenuSearchHostTag(),
                IsEnabled = false,
                Visibility = Visibility.Collapsed,
            };
            mainContextMenu.Items.Insert(0, _menuSearchResultsHost);
        }

        internal TextBox menuSearchBox
        {
            get
            {
                EnsureMenuSearchTemplateParts();
                return _menuSearchBox!;
            }
        }

        internal TextBlock menuSearchMatchCountText
        {
            get
            {
                EnsureMenuSearchTemplateParts();
                return _menuSearchMatchCountText!;
            }
        }

        internal TextBlock menuSearchNoResultsText
        {
            get
            {
                EnsureMenuSearchTemplateParts();
                return _menuSearchNoResultsText!;
            }
        }

        internal MenuItem menuSearchResultsHost
        {
            get
            {
                EnsureMenuSearchTemplateParts();
                return _menuSearchResultsHost!;
            }
        }

        private void OnContextMenuOpened(object sender, RoutedEventArgs e)
        {
            RestoreMenuSearchVisibility();
            menuSearchBox.Text = string.Empty;
            _contextMenuController.HandleOpened();
            PrepareContextualRoiMenu();
            ApplyMenuAvailabilityHints();
            menuSearchBox.Dispatcher.BeginInvoke(
                () => menuSearchBox.Focus(),
                DispatcherPriority.Input);
        }

        private void OnMenuSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            string rawQuery = menuSearchBox.Text.Trim();
            string[] words = rawQuery.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

            if (words.Length == 0)
            {
                RestoreMenuSearchVisibility();
                ClearMenuSearchResults();
                menuSearchMatchCountText.Visibility = Visibility.Collapsed;
                menuSearchNoResultsText.Visibility = Visibility.Collapsed;
                return;
            }

            CaptureMenuSearchVisibility(mainContextMenu);
            List<MenuSearchCommand> results = MenuSearchCommands.Find(
                mainContextMenu.Items.Cast<object>(),
                words,
                menuItem => GetMenuSearchBaseVisibility(menuItem) != Visibility.Collapsed).ToList();
            foreach (object item in mainContextMenu.Items)
            {
                if (item is MenuItem menuItem && menuItem.Tag is not (MenuSearchHostTag or MenuSearchResultTag))
                {
                    menuItem.Visibility = Visibility.Collapsed;
                }
                else if (item is Separator separator)
                {
                    separator.Visibility = Visibility.Collapsed;
                }
            }

            UpdateMenuSearchResults(results);
            menuSearchMatchCountText.Text = UiText.Format("MenuSearchMatchCount", results.Count);
            menuSearchMatchCountText.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            menuSearchNoResultsText.Visibility = results.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnMenuSearchPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                ClearMenuSearchOrClose();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Down && TryFocusFirstVisibleMenuItem())
            {
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter && TryInvokeSingleVisibleMenuItem())
            {
                e.Handled = true;
            }
        }

        private void ClearMenuSearchOrClose()
        {
            if (!string.IsNullOrEmpty(menuSearchBox.Text))
            {
                menuSearchBox.Clear();
                return;
            }

            mainContextMenu.IsOpen = false;
        }

        private bool TryFocusFirstVisibleMenuItem()
        {
            if (_menuSearchResultsHost?.Visibility == Visibility.Visible)
            {
                MenuItem? result = _menuSearchResultItems.FirstOrDefault(item => item.IsEnabled);
                if (result is not null)
                {
                    result.Focus();
                    return true;
                }
            }

            foreach (MenuItem menuItem in EnumerateMenuItems(mainContextMenu))
            {
                if (menuItem.Items.Count == 0 && menuItem.Visibility == Visibility.Visible && menuItem.IsEnabled && menuItem.Tag is not MenuSearchResultTag)
                {
                    menuItem.Focus();
                    return true;
                }
            }

            return false;
        }

        private bool TryInvokeSingleVisibleMenuItem()
        {
            if (string.IsNullOrWhiteSpace(menuSearchBox.Text))
            {
                return false;
            }

            List<MenuItem> matches = EnumerateMenuItems(mainContextMenu)
                .Where(item => item.Items.Count == 0 && item.Visibility == Visibility.Visible && item.IsEnabled && item.Tag is MenuSearchResultTag)
                .Take(2)
                .ToList();
            if (matches.Count != 1)
            {
                return false;
            }

            InvokeMenuSearchResult(matches[0]);
            return true;
        }

        private void UpdateMenuSearchResults(IReadOnlyList<MenuSearchCommand> results)
        {
            MenuItem host = _menuSearchResultsHost!;
            ClearMenuSearchResultItems();
            host.Header = UiText.Get("MenuSearchResults");
            int insertionIndex = mainContextMenu.Items.IndexOf(host) + 1;
            foreach (MenuSearchCommand result in results)
            {
                MenuItem copy = new()
                {
                    Header = result.Path,
                    Tag = new MenuSearchResultTag(result.Original),
                    IsEnabled = result.IsEnabled,
                    ToolTip = result.IsEnabled ? result.Original.ToolTip : result.DisabledReason ?? UiText.Get("MenuUnavailable"),
                    InputGestureText = result.Original.InputGestureText,
                };
                copy.Click += OnMenuSearchResultClick;
                ToolTipService.SetShowOnDisabled(copy, true);
                _menuSearchResultItems.Add(copy);
                mainContextMenu.Items.Insert(insertionIndex++, copy);
            }

            host.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ClearMenuSearchResults()
        {
            if (_menuSearchResultsHost is null)
            {
                return;
            }

            ClearMenuSearchResultItems();
            _menuSearchResultsHost.Visibility = Visibility.Collapsed;
        }

        private void ClearMenuSearchResultItems()
        {
            foreach (MenuItem item in _menuSearchResultItems)
            {
                mainContextMenu.Items.Remove(item);
            }

            _menuSearchResultItems.Clear();
        }

        private void OnMenuSearchResultClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { Tag: MenuSearchResultTag tag, IsEnabled: true })
            {
                InvokeMenuSearchResult((MenuItem)sender);
            }
        }

        private void InvokeMenuSearchResult(MenuItem result)
        {
            if (result.Tag is MenuSearchResultTag { Original.IsEnabled: true } tag)
            {
                mainContextMenu.IsOpen = false;
                tag.Original.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
        }

        private void CaptureMenuSearchVisibility(ItemsControl itemsControl)
        {
            foreach (object item in itemsControl.Items)
            {
                if (item is MenuItem menuItem)
                {
                    if (menuItem.Tag is MenuSearchHostTag or MenuSearchResultTag)
                    {
                        continue;
                    }
                    CaptureMenuSearchVisibility(menuItem);
                }
                else if (item is Separator separator)
                {
                    CaptureMenuSearchVisibility(separator);
                }
            }
        }

        private void CaptureMenuSearchVisibility(DependencyObject element)
        {
            if (!_menuSearchVisibilitySnapshots.ContainsKey((UIElement)element))
            {
                _menuSearchVisibilitySnapshots[(UIElement)element] = new(
                    BindingOperations.GetBindingBase(element, UIElement.VisibilityProperty),
                    ((UIElement)element).Visibility);
            }
        }

        private void CaptureMenuSearchVisibility(MenuItem menuItem)
        {
            CaptureMenuSearchVisibility((DependencyObject)menuItem);
            foreach (object child in menuItem.Items)
            {
                if (child is MenuItem nested)
                {
                    CaptureMenuSearchVisibility(nested);
                }
                else if (child is Separator separator)
                {
                    CaptureMenuSearchVisibility(separator);
                }
            }
        }

        private Visibility GetMenuSearchBaseVisibility(UIElement element)
        {
            return _menuSearchVisibilitySnapshots.TryGetValue(element, out MenuSearchVisibilitySnapshot? snapshot)
                ? snapshot.Value
                : element.Visibility;
        }

        private void RestoreMenuSearchVisibility()
        {
            foreach ((UIElement element, MenuSearchVisibilitySnapshot snapshot) in _menuSearchVisibilitySnapshots)
            {
                if (snapshot.Binding is not null)
                {
                    BindingOperations.SetBinding(element, UIElement.VisibilityProperty, snapshot.Binding);
                }
                else
                {
                    ((UIElement)element).Visibility = snapshot.Value;
                }
            }

            _menuSearchVisibilitySnapshots.Clear();
        }
    }
}
