using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ImageViewer.Models;
using ImageViewer.Rendering;
using ImageViewer.Services;
using ImageViewer.Utils;
using ImageViewer.ViewModels;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        private void UpdatePixelGrid()
        {
            if (!ShowPixelGrid || Scale < PixelGridScaleThreshold || !ImageViewerImageSourceUtilities.TryGetSourceImageSize(ImageSource, out Size imageSize))
            {
                if (_pixelGridPath != null)
                {
                    _pixelGridPath.Visibility = Visibility.Collapsed;
                }

                return;
            }

            _pixelGridPath ??= new Path
            {
                Stroke = Brushes.Gray,
                IsHitTestVisible = false
            };

            if (!pixelGridCanvas.Children.Contains(_pixelGridPath))
            {
                pixelGridCanvas.Children.Clear();
                pixelGridCanvas.Children.Add(_pixelGridPath);
            }

            double width = imageSize.Width;
            double height = imageSize.Height;
            bool needsRebuild = !ReferenceEquals(_cachedPixelGridImageSource, ImageSource)
                || _cachedPixelGridWidth != width
                || _cachedPixelGridHeight != height
                || _pixelGridPath.Data == null;

            if (needsRebuild)
            {
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    for (int x = 0; x <= (int)Math.Ceiling(width); x++)
                    {
                        ctx.BeginFigure(new Point(x, 0), false, false);
                        ctx.LineTo(new Point(x, height), true, false);
                    }

                    for (int y = 0; y <= (int)Math.Ceiling(height); y++)
                    {
                        ctx.BeginFigure(new Point(0, y), false, false);
                        ctx.LineTo(new Point(width, y), true, false);
                    }
                }

                geometry.Freeze();
                _pixelGridPath.Data = geometry;
                _cachedPixelGridImageSource = ImageSource;
                _cachedPixelGridWidth = width;
                _cachedPixelGridHeight = height;
            }

            _pixelGridPath.StrokeThickness = 1 / Scale;
            _pixelGridPath.Visibility = Visibility.Visible;
        }

        internal void DrawRois(bool immediate = false, bool forceAnalysis = false)
        {
            RequestViewportOverlayRefresh(immediate);
            RequestAnalysisRefresh(forceAnalysis, immediate: immediate && forceAnalysis);
        }

        /// <summary>
        /// 可视区域对应的图像坐标矩形。
        /// Chinese: 超出可视区域的 ROI 不会出现在屏幕上，渲染它们纯属浪费——但 ROI 的标注/手柄会越出包围盒，
        /// 所以这里留了 32 个屏幕像素的余量（换算回图像单位）。
        /// English: The image-space rect covered by the viewport, padded by a screen margin so labels and handles of
        /// partially visible ROIs are not clipped away.
        /// </summary>
        internal Rect GetVisibleImageRect(Size imageSize)
        {
            Rect bounds = GetImageViewportBounds(margin: 32);
            return bounds.IsEmpty ? new Rect(imageSize) : bounds;
        }

        private Rect GetImageViewportBounds(double margin = 0)
        {
            var imageToScreen = imageContainer.RenderTransform.Value;
            Size viewport = rootGrid.RenderSize;
            if (!imageToScreen.HasInverse || viewport.Width <= 0 || viewport.Height <= 0)
            {
                return Rect.Empty;
            }
            var viewportRect = new Rect(viewport);
            viewportRect.Inflate(margin, margin);
            imageToScreen.Invert();
            return new MatrixTransform(imageToScreen).TransformBounds(viewportRect);
        }

        // The tile renderer accepts a scale/translation viewport. Feed it the
        // equivalent image-space rectangle before the WPF transform rotates or mirrors the tile.
        private Point GetTileRenderTranslation()
        {
            Rect bounds = GetImageViewportBounds();
            return bounds.IsEmpty ? new Point(translateTransform.X, translateTransform.Y) : new Point(-bounds.X * Scale, -bounds.Y * Scale);
        }

        private Size GetTileRenderViewportSize()
        {
            Rect bounds = GetImageViewportBounds();
            return bounds.IsEmpty ? rootGrid.RenderSize : new Size(bounds.Width * Scale, bounds.Height * Scale);
        }

        private void RefreshViewportOverlay()
        {
            var vm = ViewModel;
            ScreenOverlayCanvas.Children.Clear();
            DrawCommittedRois(vm);
            DrawSelectedRoi(vm);
            DrawActiveRois();
        }

        private void DrawSelectedRoiLayer()
        {
            DrawRois(immediate: true, forceAnalysis: true);
        }

        private void DrawCommittedRois(ImageViewerViewModel vm)
        {
            committedOverlayCanvas.Children.Clear();
            var context = CreateRoiRenderContext(committedOverlayCanvas);

            // 可视区域裁剪：包围盒已知且完全在可视区域之外的 ROI 跳过；包围盒未知（新类型）一律照常渲染，
            // 因为 Rect.Empty 在 GetRoiBounds 里表示“类型未知”，把它当成零尺寸会让新 ROI 直接消失。
            Rect visible = GetVisibleImageRect(ImageViewerImageSourceUtilities.TryGetSourceImageSize(ImageSource, out Size imageSize)
                ? imageSize
                : new Size(0, 0));

            foreach (RoiBase roi in vm.AllRois)
            {
                if (ReferenceEquals(roi, vm.SelectedRoi))
                {
                    continue;
                }

                Rect bounds = ImageAnalysisService.GetRoiBounds(roi);
                if (!bounds.IsEmpty && !bounds.IntersectsWith(visible))
                {
                    continue;
                }

                RoiRenderer.Render(roi, context, null, false);
            }
        }

        private void DrawSelectedRoi(ImageViewerViewModel vm)
        {
            selectionOverlayCanvas.Children.Clear();
            var context = CreateRoiRenderContext(selectionOverlayCanvas);
            RoiRenderer.RenderSelected(vm.SelectedRoi, context);
        }

        private void DrawActiveRois()
        {
            activeOverlayCanvas.Children.Clear();
            var context = CreateRoiRenderContext(activeOverlayCanvas);

            if (ActiveDrawRoi is RoiBase activeRoi)
            {
                RoiRenderer.RenderActive([activeRoi], context);
            }

            // 插件化绘制会话自行绘制"画到一半"的预览（橡皮筋、闭合虚线、实时数值文本）。
            _activeDrawSession?.DrawOverlay(DrawHost, context);
        }
    }
}
