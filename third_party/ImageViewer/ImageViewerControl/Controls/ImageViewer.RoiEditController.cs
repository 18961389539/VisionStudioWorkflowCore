using System;
using System.Windows.Media;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.ViewModels;

namespace ImageViewer.Controls
{
    internal sealed class RoiEditController
    {
        private readonly ImageViewerViewModel _viewModel;
        private readonly ImageViewerDialogWorkflowService _dialogWorkflowService;
        private readonly Action<RoiBase?> _tryRefreshCaliperDetection;
        private readonly Action _drawRois;
        private readonly Action<string, Exception> _logNonCriticalError;
        private readonly Action<string, StatusHintKind> _showStatusHint;

        public RoiEditController(
            ImageViewerViewModel viewModel,
            ImageViewerDialogWorkflowService dialogWorkflowService,
            Action<RoiBase?> tryRefreshCaliperDetection,
            Action drawRois,
            Action<string, Exception> logNonCriticalError,
            Action<string, StatusHintKind> showStatusHint)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _dialogWorkflowService = dialogWorkflowService ?? throw new ArgumentNullException(nameof(dialogWorkflowService));
            _tryRefreshCaliperDetection = tryRefreshCaliperDetection ?? throw new ArgumentNullException(nameof(tryRefreshCaliperDetection));
            _drawRois = drawRois ?? throw new ArgumentNullException(nameof(drawRois));
            _logNonCriticalError = logNonCriticalError ?? throw new ArgumentNullException(nameof(logNonCriticalError));
            _showStatusHint = showStatusHint ?? throw new ArgumentNullException(nameof(showStatusHint));
        }

        public bool RemoveSelectedRoi()
        {
            if (_viewModel.SelectedRoi == null)
            {
                return false;
            }

            _viewModel.UndoRedo.Execute(new RemoveRoiCommand(_viewModel.SelectedRoi, _viewModel));
            return true;
        }

        public void Undo()
        {
            if (!_viewModel.UndoRedo.CanUndo)
            {
                return;
            }

            // 撤销失败时管理器不改动栈（命令仍在撤销栈，可重试），但异常不能让键盘/菜单路径崩溃：
            // 这里记录并给出状态提示，界面保持与历史一致。
            if (!TryRunHistoryOperation(() => _viewModel.UndoRedo.Undo(), "Undo"))
            {
                return;
            }

            _tryRefreshCaliperDetection(_viewModel.SelectedRoi);
            _drawRois();
            _showStatusHint(UiText.Get("StatusUndone"), StatusHintKind.Info);
        }

        public void Redo()
        {
            if (!_viewModel.UndoRedo.CanRedo)
            {
                return;
            }

            if (!TryRunHistoryOperation(() => _viewModel.UndoRedo.Redo(), "Redo"))
            {
                return;
            }

            _tryRefreshCaliperDetection(_viewModel.SelectedRoi);
            _drawRois();
            _showStatusHint(UiText.Get("StatusRedone"), StatusHintKind.Info);
        }

        /// <summary>
        /// 执行一次撤销/重做，捕获异常并转为日志 + 状态提示。
        /// Chinese: 返回 false 表示操作失败；调用方应跳过后续的界面刷新与"已完成"提示。
        /// English: Runs a history operation, converting any failure into a log entry plus a status hint.
        /// </summary>
        private bool TryRunHistoryOperation(Action operation, string operationName)
        {
            try
            {
                operation();
                return true;
            }
            catch (Exception ex)
            {
                _logNonCriticalError($"历史操作失败：{operationName}", ex);
                _showStatusHint(UiText.Get("StatusUndoRedoFailed"), StatusHintKind.Error);
                return false;
            }
        }

        public void DeleteSelected()
        {
            if (RemoveSelectedRoi())
            {
                _drawRois();
                _showStatusHint(UiText.Get("StatusRoiDeleted"), StatusHintKind.Info);
            }
        }

        public void ClearAll()
        {
            if (_viewModel.AllRois.Count == 0)
            {
                return;
            }

            _viewModel.UndoRedo.Execute(new ClearAllRoisCommand(_viewModel));
            _drawRois();
            _showStatusHint(UiText.Get("StatusRoisCleared"), StatusHintKind.Info);
        }

        public void SetSelectedLabel()
        {
            if (_viewModel.SelectedRoi is not RoiBase roi)
            {
                return;
            }

            string? label = _dialogWorkflowService.ShowRoiLabelDialog(roi);
            if (label == null)
            {
                return;
            }

            _viewModel.UndoRedo.Execute(new RoiLabelCommand(roi, label));
            _drawRois();
        }

        public void SetSelectedColor(RoiColor color)
        {
            if (_viewModel.SelectedRoi is not RoiBase roi)
            {
                return;
            }

            _viewModel.UndoRedo.Execute(new RoiColorCommand(roi, color));
            _drawRois();
        }
    }
}
