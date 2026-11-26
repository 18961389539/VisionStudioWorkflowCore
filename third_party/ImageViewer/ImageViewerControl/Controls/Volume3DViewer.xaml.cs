using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Geometry;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Services;

namespace ImageViewer.Controls
{
    public partial class Volume3DViewer : UserControl, IDisposable, IAsyncDisposable
    {
        private VolumeData? _volume;
        private MeshGeometryModel3D? _volumeBoundsModel;
        private MeshGeometryModel3D? _axialPlaneModel;
        private MeshGeometryModel3D? _coronalPlaneModel;
        private MeshGeometryModel3D? _sagittalPlaneModel;
        private MeshGeometryModel3D? _cropBoundsModel;
        private VolumeCropBounds? _cropBounds;
        private double _volumeOpacity = 0.22;
        private bool _showBoundingBox = true;
        private bool _showAxialPlane = true;
        private bool _showCoronalPlane;
        private bool _showSagittalPlane;
        private bool _isDisposed;
        private TextBox? _menuSearchBox;
        private TextBlock? _menuSearchMatchCountText;
        private TextBlock? _menuSearchNoResultsText;

        public event EventHandler? SwitchToAxialSliceRequested;
        public event EventHandler<VolumeVoxelPickedEventArgs>? VoxelPicked;

        public Volume3DViewer()
        {
            InitializeComponent();
                viewport.Camera = new HelixToolkit.Wpf.SharpDX.PerspectiveCamera
            {
                Position = new Point3D(0, 0, 3),
                LookDirection = new Vector3D(0, 0, -3),
                UpDirection = new Vector3D(0, 1, 0),
                FieldOfView = 45
            };
            viewport.EffectsManager = new DefaultEffectsManager();
            viewport.Items.Add(new DirectionalLight3D { Direction = new Vector3D(-1, -1, -1) });
            volumeStatusText.Text = UiText.Get("StatusNoVolume");
            ApplyTheme();
        }

        /// <summary>
        /// 控件主题（默认深色）。
        /// Chinese: 内部配色通过 DynamicResource 引用主题键，切换即时生效；含 Helix 视口背景色。
        /// English: Theme of this control, including the Helix viewport background color.
        /// </summary>
        public static readonly DependencyProperty ThemeProperty =
            DependencyProperty.Register(
                nameof(Theme),
                typeof(ViewerThemes.Theme),
                typeof(Volume3DViewer),
                new PropertyMetadata(ViewerThemes.Theme.Dark, OnThemeChanged));

        /// <summary>自定义主题字典；非 null 时优先于 <see cref="Theme"/>。</summary>
        public static readonly DependencyProperty ThemeDictionaryProperty =
            DependencyProperty.Register(
                nameof(ThemeDictionary),
                typeof(ResourceDictionary),
                typeof(Volume3DViewer),
                new PropertyMetadata(null, OnThemeChanged));

        /// <summary>控件主题（默认深色）。</summary>
        public ViewerThemes.Theme Theme
        {
            get => (ViewerThemes.Theme)GetValue(ThemeProperty);
            set => SetValue(ThemeProperty, value);
        }

        /// <summary>自定义主题字典；非 null 时优先于 <see cref="Theme"/>。</summary>
        public ResourceDictionary? ThemeDictionary
        {
            get => (ResourceDictionary?)GetValue(ThemeDictionaryProperty);
            set => SetValue(ThemeDictionaryProperty, value);
        }

        private static void OnThemeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is Volume3DViewer viewer)
            {
                viewer.ApplyTheme();
            }
        }

        private void ApplyTheme()
        {
            if (!IsInitialized)
            {
                return;
            }

            ViewerThemeSwitcher.Apply(this, ThemeDictionary ?? ViewerThemes.CreateDictionary(Theme));
        }

        public VolumeData? Volume
        {
            get => _volume;
            set
            {
                ObjectDisposedException.ThrowIf(_isDisposed, this);
                _volume = value;
                RebuildScene();
            }
        }

        public int CurrentSliceIndex { get; private set; } = -1;
        public int CurrentCoronalSliceIndex { get; private set; } = -1;
        public int CurrentSagittalSliceIndex { get; private set; } = -1;

        public VolumeCropBounds? CropBounds
        {
            get => _cropBounds;
            set
            {
                ObjectDisposedException.ThrowIf(_isDisposed, this);
                if (_volume == null)
                {
                    _cropBounds = null;
                    RemoveCropBoundsModel();
                    return;
                }

                _cropBounds = VolumeInteractionService.NormalizeCrop(_volume, value);
                UpdateCropBoundsModel();
            }
        }

        public void SetCurrentSlice(int sliceIndex)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_volume == null)
            {
                CurrentSliceIndex = -1;
                return;
            }

            CurrentSliceIndex = Math.Clamp(sliceIndex, 0, _volume.Depth - 1);
            UpdatePlanes();
        }

        public void SetCoronalSlice(int sliceIndex)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_volume == null)
            {
                CurrentCoronalSliceIndex = -1;
                return;
            }

            CurrentCoronalSliceIndex = Math.Clamp(sliceIndex, 0, _volume.Height - 1);
            UpdatePlanes();
        }

        public void SetSagittalSlice(int sliceIndex)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_volume == null)
            {
                CurrentSagittalSliceIndex = -1;
                return;
            }

            CurrentSagittalSliceIndex = Math.Clamp(sliceIndex, 0, _volume.Width - 1);
            UpdatePlanes();
        }

        public Viewport3DX SceneViewport => viewport;

        public bool TryPickVoxelAtWorldPoint(Point3D point)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_volume == null || !VolumeInteractionService.TryReadVoxelAtWorldPoint(_volume, point, out VolumeVoxelLocation? voxel) || voxel == null)
            {
                return false;
            }

            VoxelPicked?.Invoke(this, new VolumeVoxelPickedEventArgs(voxel));
            return true;
        }

        public bool ShowCoordinateSystem
        {
            get => viewport.ShowCoordinateSystem;
            set => viewport.ShowCoordinateSystem = value;
        }

        public bool ShowBoundingBox
        {
            get => _showBoundingBox;
            set
            {
                _showBoundingBox = value;
                if (_volumeBoundsModel != null)
                {
                    _volumeBoundsModel.Visibility = value ? Visibility.Visible : Visibility.Hidden;
                }
            }
        }

        public double VolumeOpacity
        {
            get => _volumeOpacity;
            set
            {
                _volumeOpacity = Math.Clamp(value, 0.05, 0.9);
                if (_volumeBoundsModel?.Material is PhongMaterial material)
                {
                    material.DiffuseColor = new Color4(0.18f, 0.55f, 0.82f, (float)_volumeOpacity);
                }
            }
        }

        public bool ShowAxialPlane
        {
            get => _showAxialPlane;
            set
            {
                _showAxialPlane = value;
                UpdatePlaneVisibility();
            }
        }

        public bool ShowCoronalPlane
        {
            get => _showCoronalPlane;
            set
            {
                _showCoronalPlane = value;
                UpdatePlaneVisibility();
            }
        }

        public bool ShowSagittalPlane
        {
            get => _showSagittalPlane;
            set
            {
                _showSagittalPlane = value;
                UpdatePlaneVisibility();
            }
        }

        public void ResetCamera()
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            SetCamera(new Point3D(0, 0, 3), new Vector3D(0, 0, -3), new Vector3D(0, 1, 0));
        }

        public void FitVolume() => SetVolumeCamera(new Vector3D(1.6, 1.4, 1.8));

        public void ShowFrontView() => SetVolumeCamera(new Vector3D(0, 0, 1));

        public void ShowBackView() => SetVolumeCamera(new Vector3D(0, 0, -1));

        public void ShowTopView() => SetVolumeCamera(new Vector3D(0, 1, 0));

        public void ShowBottomView() => SetVolumeCamera(new Vector3D(0, -1, 0));

        public void ShowSideView() => SetVolumeCamera(new Vector3D(1, 0, 0));

        public void ShowOppositeSideView() => SetVolumeCamera(new Vector3D(-1, 0, 0));

        public void ShowIsometricView() => SetVolumeCamera(new Vector3D(1, 1, 1));

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            viewport.Items.Clear();
            viewport.EffectsManager?.Dispose();
            viewport.EffectsManager = null;
            _volumeBoundsModel = null;
            _axialPlaneModel = null;
            _coronalPlaneModel = null;
            _sagittalPlaneModel = null;
            _volume = null;
            GC.SuppressFinalize(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }

        private void RebuildScene()
        {
            viewport.Items.Clear();
            _volumeBoundsModel = null;
            _axialPlaneModel = null;
            _coronalPlaneModel = null;
            _sagittalPlaneModel = null;
            _cropBoundsModel = null;
            CropBounds = null;
            if (_volume == null)
            {
                volumeStatusText.Text = UiText.Get("StatusNoVolume");
                return;
            }

            double width = _volume.Width * _volume.SpacingX;
            double height = _volume.Height * _volume.SpacingY;
            double depth = _volume.Depth * _volume.SpacingZ;
            MeshBuilder meshBuilder = new();
            meshBuilder.AddBox(
                new Vector3(0, 0, 0),
                (float)width,
                (float)height,
                (float)depth);

            _volumeBoundsModel = new MeshGeometryModel3D
            {
                Geometry = meshBuilder.ToMeshGeometry3D(),
                Material = new PhongMaterial
                {
                    DiffuseColor = new Color4(0.18f, 0.55f, 0.82f, (float)_volumeOpacity),
                    AmbientColor = new Color4(0.08f, 0.18f, 0.28f, 1),
                    ReflectiveColor = new Color4(0, 0, 0, 1),
                    SpecularColor = new Color4(0.2f, 0.2f, 0.2f, 1),
                    SpecularShininess = 16
                },
                IsTransparent = true
            };
            _volumeBoundsModel.Visibility = _showBoundingBox ? Visibility.Visible : Visibility.Hidden;
            viewport.Items.Add(_volumeBoundsModel);
            CurrentSliceIndex = 0;
            CurrentCoronalSliceIndex = _volume.Height / 2;
            CurrentSagittalSliceIndex = _volume.Width / 2;
            UpdatePlanes();
            UpdateCropBoundsModel();
            viewport.Camera = new HelixToolkit.Wpf.SharpDX.PerspectiveCamera
            {
                Position = new Point3D(width * 1.6, height * 1.4, depth * 1.8),
                LookDirection = new Vector3D(-width * 1.6, -height * 1.4, -depth * 1.8),
                UpDirection = new Vector3D(0, 1, 0),
                FieldOfView = 45
            };
            volumeStatusText.Text = UiText.Format("Status3DVolumeSummary", _volume.Width, _volume.Height, _volume.Depth, _volume.SpacingX, _volume.SpacingY, _volume.SpacingZ);
        }

        private void OnResetCameraClick(object sender, RoutedEventArgs e) => ResetCamera();
        private void OnFitVolumeClick(object sender, RoutedEventArgs e) => FitVolume();
        private void OnFrontViewClick(object sender, RoutedEventArgs e) => ShowFrontView();
        private void OnBackViewClick(object sender, RoutedEventArgs e) => ShowBackView();
        private void OnTopViewClick(object sender, RoutedEventArgs e) => ShowTopView();
        private void OnBottomViewClick(object sender, RoutedEventArgs e) => ShowBottomView();
        private void OnSideViewClick(object sender, RoutedEventArgs e) => ShowSideView();
        private void OnOppositeSideViewClick(object sender, RoutedEventArgs e) => ShowOppositeSideView();
        private void OnIsometricViewClick(object sender, RoutedEventArgs e) => ShowIsometricView();

        private static IEnumerable<MenuItem> EnumerateMenuItems(ItemsControl itemsControl)
        {
            foreach (object item in itemsControl.Items)
            {
                if (item is not MenuItem menuItem)
                {
                    continue;
                }

                yield return menuItem;
                foreach (MenuItem child in EnumerateMenuItems(menuItem))
                {
                    yield return child;
                }
            }
        }

        private void EnsureMenuSearchTemplateParts()
        {
            if (_menuSearchBox is not null)
            {
                return;
            }

            threeDContextMenu.ApplyTemplate();
            _menuSearchBox = threeDContextMenu.Template?.FindName("PART_MenuSearchBox", threeDContextMenu) as TextBox
                ?? throw new InvalidOperationException("3D 菜单搜索框模板部件缺失。");
            _menuSearchMatchCountText = threeDContextMenu.Template?.FindName("PART_MenuSearchMatchCount", threeDContextMenu) as TextBlock
                ?? throw new InvalidOperationException("3D 菜单搜索结果模板部件缺失。");
            _menuSearchNoResultsText = threeDContextMenu.Template?.FindName("PART_MenuSearchNoResults", threeDContextMenu) as TextBlock
                ?? throw new InvalidOperationException("3D 菜单无结果模板部件缺失。");
        }

        private void OnThreeDContextMenuOpened(object sender, RoutedEventArgs e)
        {
            EnsureMenuSearchTemplateParts();
            menuSearchBox.Clear();
            menuSearchBox.Dispatcher.BeginInvoke(
                () => menuSearchBox.Focus(),
                DispatcherPriority.Input);
        }

        private TextBox menuSearchBox
        {
            get
            {
                EnsureMenuSearchTemplateParts();
                return _menuSearchBox!;
            }
        }

        private TextBlock menuSearchMatchCountText
        {
            get
            {
                EnsureMenuSearchTemplateParts();
                return _menuSearchMatchCountText!;
            }
        }

        private TextBlock menuSearchNoResultsText
        {
            get
            {
                EnsureMenuSearchTemplateParts();
                return _menuSearchNoResultsText!;
            }
        }

        private void OnThreeDMenuSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            string[] words = menuSearchBox.Text.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                foreach (MenuItem menuItem in EnumerateMenuItems(threeDContextMenu))
                {
                    menuItem.Visibility = Visibility.Visible;
                }

                foreach (object item in threeDContextMenu.Items)
                {
                    if (item is Separator separator)
                    {
                        separator.Visibility = Visibility.Visible;
                    }
                }

                menuSearchMatchCountText.Visibility = Visibility.Collapsed;
                menuSearchNoResultsText.Visibility = Visibility.Collapsed;
                return;
            }

            bool hasMatch = false;
            int matchCount = 0;
            foreach (object item in threeDContextMenu.Items)
            {
                if (item is MenuItem menuItem)
                {
                    bool itemMatch = UpdateMenuSearchVisibility(menuItem, words);
                    hasMatch |= itemMatch;
                    if (itemMatch)
                    {
                        matchCount += CountMatchingLeafMenuItems(menuItem, words);
                    }
                }
                else if (item is Separator separator)
                {
                    separator.Visibility = Visibility.Collapsed;
                }
            }

            menuSearchMatchCountText.Text = UiText.Format("MenuSearchMatchCount", matchCount);
            menuSearchMatchCountText.Visibility = hasMatch ? Visibility.Visible : Visibility.Collapsed;
            menuSearchNoResultsText.Visibility = hasMatch ? Visibility.Collapsed : Visibility.Visible;
            UpdateMenuSeparatorVisibility(threeDContextMenu);
        }

        private void OnThreeDMenuSearchPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (!string.IsNullOrEmpty(menuSearchBox.Text))
                {
                    menuSearchBox.Clear();
                }
                else
                {
                    threeDContextMenu.IsOpen = false;
                }

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

        private bool TryFocusFirstVisibleMenuItem()
        {
            foreach (MenuItem menuItem in EnumerateMenuItems(threeDContextMenu))
            {
                if (menuItem.Items.Count == 0 && menuItem.Visibility == Visibility.Visible && menuItem.IsEnabled)
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

            List<MenuItem> matches = EnumerateMenuItems(threeDContextMenu)
                .Where(item => item.Items.Count == 0 && item.Visibility == Visibility.Visible && item.IsEnabled)
                .Take(2)
                .ToList();
            if (matches.Count != 1)
            {
                return false;
            }

            matches[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            return true;
        }

        private static bool UpdateMenuSearchVisibility(MenuItem menuItem, string[] words)
        {
            bool childMatch = false;
            foreach (object child in menuItem.Items)
            {
                if (child is MenuItem childMenuItem)
                {
                    childMatch |= UpdateMenuSearchVisibility(childMenuItem, words);
                }
            }

            bool ownMatch = menuItem.Items.Count == 0 && MatchesOwnSearchTerms(menuItem, words);
            bool visible = ownMatch || childMatch;
            menuItem.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            return visible;
        }

        private static int CountMatchingLeafMenuItems(MenuItem menuItem, string[] words)
        {
            int count = 0;
            foreach (object child in menuItem.Items)
            {
                if (child is MenuItem childMenuItem)
                {
                    count += CountMatchingLeafMenuItems(childMenuItem, words);
                }
            }

            if (menuItem.Items.Count == 0 && menuItem.Visibility == Visibility.Visible && MatchesOwnSearchTerms(menuItem, words))
            {
                count++;
            }

            return count;
        }

        private static void UpdateMenuSeparatorVisibility(ItemsControl itemsControl)
        {
            for (int index = 0; index < itemsControl.Items.Count; index++)
            {
                if (itemsControl.Items[index] is not Separator separator)
                {
                    continue;
                }

                bool before = FindVisibleMenuItem(itemsControl, index, -1);
                bool after = FindVisibleMenuItem(itemsControl, index, 1);
                separator.Visibility = before && after ? Visibility.Visible : Visibility.Collapsed;
            }

            foreach (object item in itemsControl.Items)
            {
                if (item is MenuItem menuItem)
                {
                    UpdateMenuSeparatorVisibility(menuItem);
                }
            }
        }

        private static bool FindVisibleMenuItem(ItemsControl itemsControl, int separatorIndex, int direction)
        {
            for (int index = separatorIndex + direction; index >= 0 && index < itemsControl.Items.Count; index += direction)
            {
                if (itemsControl.Items[index] is MenuItem menuItem)
                {
                    return menuItem.Visibility == Visibility.Visible;
                }
            }

            return false;
        }

        private static bool MatchesAllWords(string? text, string[] words)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string normalized = NormalizeSearchText(text);
            return words.All(word => normalized.Contains(NormalizeSearchText(word), StringComparison.Ordinal));
        }

        private static bool MatchesOwnSearchTerms(MenuItem menuItem, string[] words)
        {
            return MatchesAllWords(menuItem.Header?.ToString(), words)
                || MatchesAllWords(menuItem.InputGestureText, words)
                || MatchesAllWords(menuItem.ToolTip?.ToString(), words);
        }

        private static string NormalizeSearchText(string text)
        {
            Span<char> buffer = text.Length <= 256 ? stackalloc char[text.Length] : new char[text.Length];
            int count = 0;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c) || c is '+' or '-' or '.' or '/' or '\\' or '(' or ')' or '（' or '）')
                {
                    continue;
                }

                buffer[count++] = char.ToLowerInvariant(c);
            }

            return count == text.Length ? text.ToLowerInvariant() : new string(buffer[..count]);
        }

        private void OnCoordinateSystemClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem)
            {
                ShowCoordinateSystem = menuItem.IsChecked;
            }
        }

        private void OnBoundingBoxClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem)
            {
                ShowBoundingBox = menuItem.IsChecked;
            }
        }

        private void OnAxialPlaneClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem)
            {
                ShowAxialPlane = menuItem.IsChecked;
            }
        }

        private void OnCoronalPlaneClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem)
            {
                ShowCoronalPlane = menuItem.IsChecked;
            }
        }

        private void OnSagittalPlaneClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem)
            {
                ShowSagittalPlane = menuItem.IsChecked;
            }
        }

        private void OnIncreaseTransparencyClick(object sender, RoutedEventArgs e) => VolumeOpacity -= 0.05;

        private void OnDecreaseTransparencyClick(object sender, RoutedEventArgs e) => VolumeOpacity += 0.05;

        private void OnSwitchToAxialSliceClick(object sender, RoutedEventArgs e) => SwitchToAxialSliceRequested?.Invoke(this, EventArgs.Empty);

        private void SetVolumeCamera(Vector3D direction)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_volume == null)
            {
                ResetCamera();
                return;
            }

            double width = _volume.Width * _volume.SpacingX;
            double height = _volume.Height * _volume.SpacingY;
            double depth = _volume.Depth * _volume.SpacingZ;
            double distance = Math.Max(width, Math.Max(height, depth)) * 2.4;
            Point3D position = new(direction.X * distance, direction.Y * distance, direction.Z * distance);
            Vector3D up = Math.Abs(direction.Y) > 0.9 ? new Vector3D(0, 0, -1) : new Vector3D(0, 1, 0);
            SetCamera(position, new Vector3D(-position.X, -position.Y, -position.Z), up);
        }

        private void SetCamera(Point3D position, Vector3D lookDirection, Vector3D upDirection)
        {
            viewport.Camera = new HelixToolkit.Wpf.SharpDX.PerspectiveCamera
            {
                Position = position,
                LookDirection = lookDirection,
                UpDirection = upDirection,
                FieldOfView = 45
            };
        }

        private void UpdatePlanes()
        {
            if (_volume == null || CurrentSliceIndex < 0)
            {
                return;
            }

            double width = _volume.Width * _volume.SpacingX;
            double height = _volume.Height * _volume.SpacingY;
            double depth = _volume.Depth * _volume.SpacingZ;
            double axialZ = -depth / 2 + (CurrentSliceIndex + 0.5) * _volume.SpacingZ;
            double coronalY = -height / 2 + (CurrentCoronalSliceIndex + 0.5) * _volume.SpacingY;
            double sagittalX = -width / 2 + (CurrentSagittalSliceIndex + 0.5) * _volume.SpacingX;

            _axialPlaneModel = ReplacePlane(_axialPlaneModel, new Vector3(0, 0, (float)axialZ), (float)width, (float)height, 0.01f, new Color4(0.95f, 0.72f, 0.2f, 0.35f));
            _coronalPlaneModel = ReplacePlane(_coronalPlaneModel, new Vector3(0, (float)coronalY, 0), (float)width, 0.01f, (float)depth, new Color4(0.2f, 0.85f, 0.55f, 0.3f));
            _sagittalPlaneModel = ReplacePlane(_sagittalPlaneModel, new Vector3((float)sagittalX, 0, 0), 0.01f, (float)height, (float)depth, new Color4(0.95f, 0.35f, 0.35f, 0.3f));
            UpdatePlaneVisibility();
        }

        private void UpdateCropBoundsModel()
        {
            RemoveCropBoundsModel();
            if (_volume == null || CropBounds == null)
            {
                return;
            }

            VolumeCropBounds bounds = CropBounds;
            VolumeCropBounds full = VolumeCropBounds.Full(_volume);
            if (bounds == full)
            {
                return;
            }

            double minX = bounds.MinimumX * _volume.SpacingX;
            double maxX = (bounds.MaximumX + 1) * _volume.SpacingX;
            double minY = bounds.MinimumY * _volume.SpacingY;
            double maxY = (bounds.MaximumY + 1) * _volume.SpacingY;
            double minZ = bounds.MinimumZ * _volume.SpacingZ;
            double maxZ = (bounds.MaximumZ + 1) * _volume.SpacingZ;
            MeshBuilder meshBuilder = new();
            meshBuilder.AddBox(
                new Vector3(
                    (float)((minX + maxX) / 2 - _volume.Width * _volume.SpacingX / 2),
                    (float)((minY + maxY) / 2 - _volume.Height * _volume.SpacingY / 2),
                    (float)((minZ + maxZ) / 2 - _volume.Depth * _volume.SpacingZ / 2)),
                (float)(maxX - minX),
                (float)(maxY - minY),
                (float)(maxZ - minZ));
            _cropBoundsModel = new MeshGeometryModel3D
            {
                Geometry = meshBuilder.ToMeshGeometry3D(),
                Material = new PhongMaterial
                {
                    DiffuseColor = new Color4(0.95f, 0.8f, 0.2f, 0.12f),
                    AmbientColor = new Color4(0.3f, 0.25f, 0.05f, 1),
                    ReflectiveColor = new Color4(0, 0, 0, 1),
                    SpecularColor = new Color4(0.2f, 0.2f, 0.2f, 1),
                    SpecularShininess = 4
                },
                IsTransparent = true
            };
            viewport.Items.Add(_cropBoundsModel);
        }

        private void RemoveCropBoundsModel()
        {
            if (_cropBoundsModel == null)
            {
                return;
            }

            viewport.Items.Remove(_cropBoundsModel);
            _cropBoundsModel = null;
        }

        private MeshGeometryModel3D ReplacePlane(MeshGeometryModel3D? previous, Vector3 center, float planeWidth, float planeHeight, float planeDepth, Color4 color)
        {
            if (previous != null)
            {
                viewport.Items.Remove(previous);
            }

            MeshBuilder meshBuilder = new();
            meshBuilder.AddBox(center, planeWidth, planeHeight, planeDepth);
            MeshGeometryModel3D model = new()
            {
                Geometry = meshBuilder.ToMeshGeometry3D(),
                Material = new PhongMaterial
                {
                    DiffuseColor = color,
                    AmbientColor = new Color4(color.Red * 0.4f, color.Green * 0.4f, color.Blue * 0.4f, 1),
                    ReflectiveColor = new Color4(0, 0, 0, 1),
                    SpecularColor = new Color4(0.3f, 0.3f, 0.3f, 1),
                    SpecularShininess = 8
                },
                IsTransparent = true
            };
            viewport.Items.Add(model);
            return model;
        }

        private void UpdatePlaneVisibility()
        {
            if (_axialPlaneModel != null)
            {
                _axialPlaneModel.Visibility = _showAxialPlane ? Visibility.Visible : Visibility.Hidden;
            }
            if (_coronalPlaneModel != null)
            {
                _coronalPlaneModel.Visibility = _showCoronalPlane ? Visibility.Visible : Visibility.Hidden;
            }
            if (_sagittalPlaneModel != null)
            {
                _sagittalPlaneModel.Visibility = _showSagittalPlane ? Visibility.Visible : Visibility.Hidden;
            }
        }
    }
}
