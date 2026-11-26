using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using ImageViewer.Abstractions;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Services;
using ImageViewer.ViewModels;

namespace ImageViewer.Controls
{
    /// <summary>
    /// 插件注册表相关的运行态：视图模型、交互服务与渲染器的唯一装配点。
    /// Chinese: 注册表变化有两条路径——① 换用另一个注册表实例（<see cref="ApplyPluginRegistry"/>）；
    /// ② 同一实例被就地修改（Register / Unregister，靠 <see cref="RoiPluginRegistry.Changed"/> 感知）。
    /// 两条都走同一套状态迁移，避免"只重建总列表、留下按类型缓存的旧集合"这类半更新状态。
    /// English: Owns the plugin-bound runtime state. Both registry changes — swapping the instance and mutating it in
    /// place — funnel through the same migration so ROI state can never end up half-updated.
    /// </summary>
    /// <summary>
    /// 一次注册表迁移的结果。
    /// </summary>
    internal sealed record ImageViewerRoiMigration(
        IReadOnlyList<RoiBase> DroppedRois,
        IReadOnlyList<RoiPersistenceData> UnresolvedPayloads);

    internal sealed class ImageViewerPluginRuntimeState
    {
        private RoiPluginRegistry _pluginRegistry;

        private readonly ConcurrentDictionary<Type, IRoiPlugin> _lastKnownPlugins = new();

        /// <summary>迁移调度器：Changed 事件的触发线程不可控（后台热加载），迁移必须经它回到 UI 线程。</summary>
        private readonly Action<Action> _migrationScheduler;
        private int _detached;

        public ImageViewerPluginRuntimeState(
            RoiPluginRegistry pluginRegistry,
            ISelectedRoiDetectionService selectedRoiDetectionService,
            Action<Action>? migrationScheduler = null)
        {
            _pluginRegistry = pluginRegistry ?? throw new ArgumentNullException(nameof(pluginRegistry));
            ArgumentNullException.ThrowIfNull(selectedRoiDetectionService);

            _migrationScheduler = migrationScheduler ?? (action => action());

            ViewModel = new ImageViewerViewModel(_pluginRegistry, selectedRoiDetectionService);
            RoiInteraction = CreateRoiInteraction(_pluginRegistry);
            RoiRenderer = CreateRoiRenderer(_pluginRegistry);
            RememberPlugins(_pluginRegistry);
            SubscribeToRegistry(_pluginRegistry);
        }

        /// <summary>
        /// 状态已按当前注册表迁移完成。
        /// Chinese: 参数包含两类信息——因注册表不再认识其类型而被移除的 ROI，以及用"最后一次见过它的插件"
        /// 序列化出的 unresolved 载荷（有了载荷，被移除的标注会随会话保存回写，装回插件后可恢复）。
        /// English: Raised after a migration with the dropped ROIs and their serialized unresolved payloads.
        /// </summary>
        public event Action<ImageViewerRoiMigration>? RoiStateMigrated;

        public RoiPluginRegistry PluginRegistry => _pluginRegistry;

        /// <summary>最近一次迁移的结果（观测点：测试断言"迁移经调度器执行后载荷可用"）。</summary>
        public ImageViewerRoiMigration? LastMigration { get; private set; }

        public ImageViewerViewModel ViewModel { get; }

        public RoiInteractionService RoiInteraction { get; private set; }

        public Rendering.RoiRenderService RoiRenderer { get; private set; }

        /// <summary>
        /// 换用另一个注册表实例。
        /// Chinese: 换实例时重新订阅 <see cref="RoiPluginRegistry.Changed"/>；同一实例返回空列表（就地修改由事件路径处理）。
        /// English: Swaps the registry instance and re-subscribes; a same-instance call returns an empty list because
        /// in-place mutation is handled by the Changed event path.
        /// </summary>
        public IReadOnlyList<RoiBase> ApplyPluginRegistry(RoiPluginRegistry pluginRegistry)
        {
            ArgumentNullException.ThrowIfNull(pluginRegistry);
            if (ReferenceEquals(_pluginRegistry, pluginRegistry))
            {
                return [];
            }

            UnsubscribeFromRegistry(_pluginRegistry);
            _pluginRegistry = pluginRegistry;
            SubscribeToRegistry(pluginRegistry);
            return MigrateRoiState();
        }

        /// <summary>
        /// 断开注册表订阅。
        /// Chinese: 注册表实例可能被多个 viewer 共享，viewer 释放后必须断订阅，否则注册表会一直持有
        /// ViewModel → 控件 → 整棵可视树，造成不释放。
        /// English: Detaches from the registry. A registry instance can be shared by several viewers, so a disposed
        /// viewer must unsubscribe or the registry keeps the whole control graph alive.
        /// </summary>
        public void Detach()
        {
            Volatile.Write(ref _detached, 1);
            UnsubscribeFromRegistry(_pluginRegistry);
        }


        private void SubscribeToRegistry(RoiPluginRegistry registry) => registry.Changed += OnPluginRegistryChanged;

        private void UnsubscribeFromRegistry(RoiPluginRegistry registry) => registry.Changed -= OnPluginRegistryChanged;

        private void OnPluginRegistryChanged(object? sender, EventArgs e)
        {
            if (Volatile.Read(ref _detached) != 0)
            {
                return;
            }

            // Changed 在调用方线程同步触发（后台热加载/卸载即后台线程），而迁移会修改 ObservableCollection
            // 并刷新 WPF 视觉——必须经调度器回到创建本状态的线程。换实例路径（ApplyPluginRegistry）本来就在
            // UI 线程上，不经调度器。
            _migrationScheduler(() =>
            {
                if (Volatile.Read(ref _detached) == 0)
                {
                    MigrateRoiState();
                }
            });
        }

        private IReadOnlyList<RoiBase> MigrateRoiState()
        {
            // 先记住当前注册表里每个类型的插件：被移除类型的 DTO 必须用"最后一次见过它的插件"生成，
            // 因为迁移发生时插件可能已经离开注册表（就地 Unregister 的 Changed 在删除之后触发）。
            RememberPlugins(_pluginRegistry);

            IReadOnlyList<RoiBase> droppedRois = ViewModel.ApplyPluginRegistryChange(_pluginRegistry, [.. ViewModel.AllRois]);
            var unresolvedPayloads = new List<RoiPersistenceData>();
            foreach (RoiBase roi in droppedRois)
            {
                if (TryBuildUnresolvedPayload(roi, out RoiPersistenceData? payload))
                {
                    unresolvedPayloads.Add(payload);
                }
            }

            // 插件绑定服务必须与新注册表同步，否则渲染/命中测试会继续用旧插件的渲染器与行为。
            RoiInteraction = CreateRoiInteraction(_pluginRegistry);
            RoiRenderer = CreateRoiRenderer(_pluginRegistry);

            var migration = new ImageViewerRoiMigration(droppedRois, unresolvedPayloads);
            LastMigration = migration;
            RoiStateMigrated?.Invoke(migration);
            return droppedRois;
        }

        private void RememberPlugins(RoiPluginRegistry registry)
        {
            foreach (IRoiPlugin plugin in registry.Plugins)
            {
                _lastKnownPlugins[plugin.RoiType] = plugin;
            }
        }

        /// <summary>
        /// 把被移除的 ROI 序列化成 unresolved 载荷。
        /// Chinese: 用记忆中的插件做 ROI→DTO 转换（TypeKey 是稳定键）——装回插件后按 TypeKey 即可还原。
        /// 没有见过该类型时返回 false（例如从未参与过任何迁移的全新类型），此时标注无法挽救，只能提示。
        /// English: Serializes a dropped ROI with the remembered plugin so it can be restored once the plugin returns.
        /// </summary>
        private bool TryBuildUnresolvedPayload(RoiBase roi, out RoiPersistenceData payload)
        {
            if (!_lastKnownPlugins.TryGetValue(roi.GetType(), out IRoiPlugin? plugin))
            {
                payload = new RoiPersistenceData();
                return false;
            }

            payload = new RoiPersistenceData();
            payload.PopulateCommonState(roi, plugin.TypeKey);
            plugin.PopulatePersistenceData(roi, payload);
            return true;
        }

        private static RoiInteractionService CreateRoiInteraction(RoiPluginRegistry pluginRegistry)
        {
            return new RoiInteractionService(pluginRegistry);
        }

        private static Rendering.RoiRenderService CreateRoiRenderer(RoiPluginRegistry pluginRegistry)
        {
            return new Rendering.RoiRenderService(pluginRegistry);
        }
    }
}
