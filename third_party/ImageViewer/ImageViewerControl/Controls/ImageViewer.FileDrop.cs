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
using ImageViewer.Dialogs;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Plugins;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        private void OnDragOver(object sender, DragEventArgs e)
        {
            DroppedContentController.HandleDragOver(e);
            // 拖入可打开文件时显示遮罩提示"松开以打开"，否则隐藏
            dropHintOverlay.Visibility = e.Effects == DragDropEffects.Copy ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnDragLeave(object sender, DragEventArgs e) => dropHintOverlay.Visibility = Visibility.Collapsed;

        private async void OnDrop(object sender, DragEventArgs e)
        {
            dropHintOverlay.Visibility = Visibility.Collapsed;
            await RunUiOperationAsync("拖放打开图像", () => _droppedContentController.HandleDropAsync(e));
        }

        private async void OnRetryImageLoadClick(object sender, RoutedEventArgs e)
        {
            await RunUiOperationAsync("重试加载图像", RetryLastImageLoadAsync);
        }

        private void OnCancelImageLoadClick(object sender, RoutedEventArgs e)
        {
            CancelImageLoad();
        }

        private void OnDismissDiagnosticErrorClick(object sender, RoutedEventArgs e)
        {
            DismissDiagnosticError();
        }

        private async void OnRecoverAutoSaveClick(object sender, RoutedEventArgs e)
        {
            await RunUiOperationAsync(
                "恢复自动保存",
                () => _controlComposition.SessionController.RecoverAutoSaveAsync(candidates =>
                {
                    if (Window.GetWindow(this) is not { } owner)
                    {
                        return null;
                    }
                    var dialog = new RecoverySnapshotDialog(owner, candidates);
                    return dialog.ShowDialog() == true ? dialog.SelectedSnapshot : null;
                }));
        }

        private void OnDismissRecoveryClick(object sender, RoutedEventArgs e)
        {
            _controlComposition.SessionController.DismissRecoveryPrompt();
        }
    }
}
