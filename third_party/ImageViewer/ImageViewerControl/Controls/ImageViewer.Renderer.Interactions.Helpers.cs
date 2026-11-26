using ImageViewer.Models;
using ImageViewer.ViewModels;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        /// <summary>
        /// 把 ROI 提交进集合（经撤销栈）。
        /// Chinese: 由 IRoiDrawHost.Commit 调用，是绘制结果的唯一提交入口。
        /// English: Commits an ROI into its collection through the undo stack. This is the single commit
        /// entry point used by IRoiDrawHost.Commit.
        /// </summary>
        private void CommitRoi(RoiBase roi)
        {
            var vm = ViewModel;
            vm.UndoRedo.Execute(new AddRoiCommand(roi, vm));
        }
    }
}
