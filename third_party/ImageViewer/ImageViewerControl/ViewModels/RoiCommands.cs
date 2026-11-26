using System.Collections.Generic;
using System.Windows.Media;
using ImageViewer.Models;
using System.Linq;

namespace ImageViewer.ViewModels
{
    /// <summary>
    /// ROI 状态命令（完整状态替换）
    /// Chinese: 保存 ROI 对象的完整状态（Clone），在撤销/重做时应用整个状态对象。
    /// English: Replaces the ROI's entire state with a saved state object when executed/undone.
    /// </summary>
    public class RoiStateCommand : IUndoRedoCommand
    {
        private readonly RoiBase _roi;
        private readonly RoiBase _oldState;
        private readonly RoiBase _newState;

        public RoiStateCommand(RoiBase roi, RoiBase oldState, RoiBase newState)
        {
            _roi = roi;
            _oldState = oldState;
            _newState = newState;
        }

        /// <summary>
        /// 构造器
        /// Chinese: 初始化 RoiStateCommand，接收目标 ROI 与旧/新状态对象。
        /// English: Initializes the command with the target ROI and the previous and next states.
        /// </summary>
        /// <param name="roi">目标 ROI / Target ROI</param>
        /// <param name="oldState">旧状态 / Old state snapshot</param>
        /// <param name="newState">新状态 / New state snapshot</param>

        public void Execute() => ApplyState(_newState);
        public void Undo() => ApplyState(_oldState);

        private void ApplyState(RoiBase source)
        {
            _roi.ApplyFrom(source);
        }
    }

    /// <summary>
    /// 添加 ROI 命令
    /// Chinese: 将 ROI 添加到集合并设置为选中状态，支持撤销（移除）。
    /// English: Adds an ROI to a collection and sets it as selected; Undo removes it.
    /// </summary>
    public class AddRoiCommand : IUndoRedoCommand
    {
        private readonly RoiBase _roi;
        private readonly ImageViewerViewModel _vm;

        public AddRoiCommand(RoiBase roi, ImageViewerViewModel vm)
        {
            _roi = roi;
            _vm = vm;
        }

        /// <summary>
        /// 构造器
        /// Chinese: 初始化 AddRoiCommand，接收目标集合、要添加的 ROI 与关联的 ViewModel。
        /// English: Initializes the AddRoiCommand with the collection, ROI and ViewModel.
        /// </summary>
        /// <param name="collection">目标集合 / Target collection to add the ROI to</param>
        /// <param name="roi">要添加的 ROI / ROI to add</param>
        /// <param name="vm">关联的 ViewModel / Associated ImageViewerViewModel</param>

        public void Execute()
        {
            _vm.AddRoi(_roi);
            _vm.SelectedRoi = _roi;
        }

        public void Undo()
        {
            _vm.RemoveRoi(_roi);
            if (_vm.SelectedRoi == _roi) _vm.SelectedRoi = null;
        }
    }

    /// <summary>
    /// 移除 ROI 命令
    /// Chinese: 从集合中移除指定 ROI，支持撤销（添加回集合）。
    /// English: Removes an ROI from a collection; Undo adds it back and restores selection.
    /// </summary>
    public class RemoveRoiCommand : IUndoRedoCommand
    {
        private readonly RoiBase _roi;
        private readonly ImageViewerViewModel _vm;

        public RemoveRoiCommand(RoiBase roi, ImageViewerViewModel vm)
        {
            _roi = roi;
            _vm = vm;
        }

        /// <summary>
        /// 构造器
        /// Chinese: 初始化 RemoveRoiCommand，接收目标集合、要移除的 ROI 与关联的 ViewModel。
        /// English: Initializes the RemoveRoiCommand with the collection, ROI and ViewModel.
        /// </summary>
        /// <param name="collection">目标集合 / Target collection to remove the ROI from</param>
        /// <param name="roi">要移除的 ROI / ROI to remove</param>
        /// <param name="vm">关联的 ViewModel / Associated ImageViewerViewModel</param>

        public void Execute()
        {
            _vm.RemoveRoi(_roi);
            if (_vm.SelectedRoi == _roi) _vm.SelectedRoi = null;
        }

        public void Undo()
        {
            _vm.AddRoi(_roi);
            _vm.SelectedRoi = _roi;
        }
    }

    /// <summary>
    /// 更新 ROI 标签命令
    /// Chinese: 用于更改 ROI 的 Label 文本，支持撤销回退到旧标签。
    /// English: Changes the ROI's Label and supports undo to restore the old value.
    /// </summary>
    public class RoiLabelCommand : IUndoRedoCommand
    {
        private readonly RoiBase _roi;
        private readonly string _oldLabel;
        private readonly string _newLabel;

        public RoiLabelCommand(RoiBase roi, string newLabel)
        {
            _roi = roi;
            _oldLabel = roi.Label;
            _newLabel = newLabel;
        }

        /// <summary>
        /// 构造器
        /// Chinese: 初始化 RoiLabelCommand，记录旧标签并设置新标签。
        /// English: Initializes the RoiLabelCommand storing the old label and the new label.
        /// </summary>
        /// <param name="roi">目标 ROI / Target ROI</param>
        /// <param name="newLabel">新的标签文本 / New label text</param>

        public void Execute() => _roi.Label = _newLabel;
        public void Undo() => _roi.Label = _oldLabel;
    }

    /// <summary>
    /// 更新 ROI 颜色命令
    /// Chinese: 更改 ROI 的描边颜色，支持撤销恢复旧颜色。
    /// English: Changes the ROI StrokeColor and supports undo to restore the previous color.
    /// </summary>
    public class RoiColorCommand : IUndoRedoCommand
    {
        private readonly RoiBase _roi;
        private readonly RoiColor _oldColor;
        private readonly RoiColor _newColor;

        public RoiColorCommand(RoiBase roi, RoiColor newColor)
        {
            _roi = roi;
            _oldColor = roi.StrokeColor;
            _newColor = newColor;
        }

        /// <summary>
        /// 构造器
        /// Chinese: 初始化 RoiColorCommand，记录旧颜色并设置新颜色。
        /// English: Initializes the RoiColorCommand storing the old and new colors.
        /// </summary>
        /// <param name="roi">目标 ROI / Target ROI</param>
        /// <param name="newColor">新的颜色 / New color</param>

        public void Execute() => _roi.StrokeColor = _newColor;
        public void Undo() => _roi.StrokeColor = _oldColor;
    }

    /// <summary>
    /// 清除所有 ROI 命令
    /// Chinese: 保存当前所有 ROI 的快照，并支持撤销恢复这些 ROI 与选中项。
    /// English: Clears all ROIs while saving a snapshot to allow undo to restore them.
    /// </summary>
    public class ClearAllRoisCommand : IUndoRedoCommand
    {
        private readonly ImageViewerViewModel _vm;
        private readonly List<RoiBase> _oldRois;
        private readonly RoiBase? _oldSelected;

        public ClearAllRoisCommand(ImageViewerViewModel vm)
        {
            _vm = vm;
            _oldRois = vm.AllRois.ToList();
            _oldSelected = vm.SelectedRoi;
        }

        /// <summary>
        /// 构造器
        /// Chinese: 初始化 ClearAllRoisCommand，并保存当前所有 ROI 的快照以便撤销时恢复。
        /// English: Initializes the ClearAllRoisCommand and takes snapshots of all current ROIs for undo.
        /// </summary>
        /// <param name="vm">目标 ViewModel / Target ImageViewerViewModel</param>

        public void Execute()
        {
            _vm.ClearAllRois();
        }

        public void Undo()
        {
            _vm.ReplaceAllRois(_oldRois);
            _vm.SelectedRoi = _oldSelected;
        }
    }
}
