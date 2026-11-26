using System;
using System.Collections.Generic;
using System.Linq;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Plugins;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        /// <summary>
        /// 插件注册表。
        /// Chinese: 允许宿主热切换（加载/卸载插件模块）。切换会触发一次明确的状态迁移：新注册表仍认识的 ROI 保留，
        /// 不再认识的被移除并提示，选中项与撤销栈按可用性处理——不会留下按类型缓存的旧集合。
        /// English: Hosts may hot-swap the registry. Every swap runs an explicit migration: known ROIs survive, unknown ones
        /// are dropped with a hint, selection and the undo stack follow availability.
        /// </summary>
        public RoiPluginRegistry PluginRegistry
        {
            get => _hostState.PluginRegistry;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                if (ReferenceEquals(_hostState.PluginRegistry, value))
                {
                    return;
                }

                _hostState.ApplyPluginRegistry(value);
            }
        }

        public IReadOnlyList<RoiToolDescriptor> AvailableDrawingTools => PluginRegistry.GetDrawingTools().ToArray();

        /// <summary>
        /// 注册表迁移完成后的界面收尾。
        /// Chinese: 刷新绘制菜单与画面（就地 Register/Unregister 也要走到这里，否则菜单会继续显示已被移除的工具）；
        /// 有 ROI 因类型不可用被移除时标记文档已改动并给出提示，避免"标注悄悄消失"。
        /// English: Refreshes the drawing menu and the rendered ROIs after a migration, and surfaces dropped ROIs so
        /// annotations never disappear silently.
        /// </summary>
        private void OnRoiStateMigrated(ImageViewerRoiMigration migration)
        {
            RefreshRoiDrawingMenuItems();
            DrawRois();

            // 被移除的 ROI 已经序列化成 unresolved 载荷：追加进会话的未识别通道，随下次保存回写，
            // 装回插件后可完整恢复。注意必须与已有载荷合并（ReportUnresolvedRois 是整体替换语义）。
            AppendUnresolvedRois(migration.UnresolvedPayloads);

            if (migration.DroppedRois.Count == 0)
            {
                return;
            }

            MarkDocumentDirty();
            ShowStatusHint(
                UiText.Format("StatusRoisDroppedByPluginChange", migration.DroppedRois.Count, BuildDroppedRoiTypeNames(migration.DroppedRois)),
                StatusHintKind.Error);
        }

        /// <summary>
        /// 追加未识别 ROI 载荷到会话的未识别通道。
        /// Chinese: 与 ReportUnresolvedRois 的"整体替换"不同，这里必须是**合并**——插件卸载的载荷不能抹掉
        /// 加载会话时留下的未识别项。
        /// English: Merges payloads; the load path replaces wholesale, which would discard previously appended payloads.
        /// </summary>
        internal void AppendUnresolvedRois(IReadOnlyList<RoiPersistenceData> payloads) =>
            _controlComposition.SessionController.AppendUnresolvedRois(payloads);

        /// <summary>
        /// 汇总被移除的 ROI 类型名。
        /// Chinese: 用本地化显示名而不是类型键——插件已经不在了，类型键取不到，而显示名不依赖插件。
        /// English: Uses localized display names: the owning plugin is gone, so type keys are unavailable, while
        /// display names do not depend on the plugin.
        /// </summary>
        private static string BuildDroppedRoiTypeNames(IReadOnlyList<RoiBase> droppedRois)
        {
            return string.Join(
                ", ",
                droppedRois
                    .Select(roi => roi.DisplayTypeName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase));
        }
    }
}
