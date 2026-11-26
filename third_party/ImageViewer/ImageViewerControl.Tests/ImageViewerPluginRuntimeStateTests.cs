using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ImageViewer.Controls;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Services;
using ImageViewer.ViewModels;
using Xunit;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// 插件热切换的状态迁移守卫。
    /// Chinese: 注册表变化有两条路径——换实例（<c>ApplyPluginRegistry</c>）与就地 Register/Unregister（Changed 事件）。
    /// 两条都必须走同一套迁移：可迁移的 ROI 保留、类型不可用的移除、按类型缓存的旧集合断开并丢弃、选中项与撤销栈对齐。
    /// English: Guards for plugin hot-swapping. Both registry change paths must run the same migration: known ROIs survive,
    /// unknown ones are dropped, stale typed collections are detached and discarded, selection and the undo stack follow.
    /// </summary>
    public class ImageViewerPluginRuntimeStateTests
    {
        [Fact]
        public void PluginRegistry_Switch_RebuildsPluginBoundServicesAndUpdatesViewModel()
        {
            RoiPluginRegistry initialRegistry = RoiPluginRegistry.CreateBuiltIn();
            RoiPluginRegistry updatedRegistry = RoiPluginRegistry.CreateBuiltIn();
            var state = new ImageViewerPluginRuntimeState(initialRegistry, SelectedRoiDetectionService.Default);

            var initialInteraction = state.RoiInteraction;
            var initialRenderer = state.RoiRenderer;

            state.ApplyPluginRegistry(updatedRegistry);

            Assert.Same(updatedRegistry, state.PluginRegistry);
            Assert.Same(updatedRegistry, state.ViewModel.PluginRegistry);
            Assert.NotSame(initialInteraction, state.RoiInteraction);
            Assert.NotSame(initialRenderer, state.RoiRenderer);
        }

        [Fact]
        public void PluginRegistry_SameInstance_DoesNotRebuildPluginBoundServices()
        {
            RoiPluginRegistry pluginRegistry = RoiPluginRegistry.CreateBuiltIn();
            var state = new ImageViewerPluginRuntimeState(pluginRegistry, SelectedRoiDetectionService.Default);

            var initialInteraction = state.RoiInteraction;
            var initialRenderer = state.RoiRenderer;

            IReadOnlyList<RoiBase> dropped = state.ApplyPluginRegistry(pluginRegistry);

            Assert.Empty(dropped);
            Assert.Same(initialInteraction, state.RoiInteraction);
            Assert.Same(initialRenderer, state.RoiRenderer);
            Assert.Same(pluginRegistry, state.ViewModel.PluginRegistry);
        }

        [Fact]
        public void ApplyPluginRegistry_KeepsKnownRoisAndDropsRoisTheNewRegistryCannotHandle()
        {
            RoiPluginRegistry fullRegistry = RoiPluginRegistry.CreateBuiltIn();
            var state = new ImageViewerPluginRuntimeState(fullRegistry, SelectedRoiDetectionService.Default);
            ImageViewerViewModel viewModel = state.ViewModel;

            var circle = new CircleRoi { Label = "keep" };
            var line = new LineMeasureRoi { Label = "drop" };
            viewModel.AddRoi(circle);
            viewModel.AddRoi(line);
            ObservableCollection<LineMeasureRoi> staleLineCollection = viewModel.GetRoiCollection<LineMeasureRoi>();

            IReadOnlyList<RoiBase> dropped = state.ApplyPluginRegistry(CreateRegistryWithout(fullRegistry, typeof(LineMeasureRoi)));

            Assert.Equal([line], dropped);
            Assert.Same(circle, Assert.Single(viewModel.AllRois));
            Assert.Same(circle, Assert.Single(viewModel.GetRoiCollection<CircleRoi>()));

            // 旧集合已断开并丢弃：它既不在缓存里，继续被旧引用改动也不会重新注入 AllRois。
            Assert.Empty(viewModel.GetRoiCollection<LineMeasureRoi>());
            staleLineCollection.Add(new LineMeasureRoi());
            Assert.Same(circle, Assert.Single(viewModel.AllRois));
        }

        [Fact]
        public void ApplyPluginRegistry_KeepsSelectionWhenTheSelectedRoiSurvives()
        {
            RoiPluginRegistry fullRegistry = RoiPluginRegistry.CreateBuiltIn();
            var state = new ImageViewerPluginRuntimeState(fullRegistry, SelectedRoiDetectionService.Default);
            var circle = new CircleRoi();
            state.ViewModel.AddRoi(circle);
            state.ViewModel.SelectedRoi = circle;

            state.ApplyPluginRegistry(CreateRegistryWithout(fullRegistry, typeof(LineMeasureRoi)));

            Assert.Same(circle, state.ViewModel.SelectedRoi);
            Assert.True(circle.IsSelected);
        }

        [Fact]
        public void ApplyPluginRegistry_ClearsSelectionAndUndoStackForDroppedRois()
        {
            RoiPluginRegistry fullRegistry = RoiPluginRegistry.CreateBuiltIn();
            var state = new ImageViewerPluginRuntimeState(fullRegistry, SelectedRoiDetectionService.Default);
            ImageViewerViewModel viewModel = state.ViewModel;
            var line = new LineMeasureRoi();
            viewModel.UndoRedo.Execute(new AddRoiCommand(line, viewModel));

            Assert.True(viewModel.UndoRedo.CanUndo);
            Assert.Same(line, viewModel.SelectedRoi);

            state.ApplyPluginRegistry(CreateRegistryWithout(fullRegistry, typeof(LineMeasureRoi)));

            Assert.Null(viewModel.SelectedRoi);
            Assert.False(line.IsSelected);

            // 撤销栈必须一起清空：插件缺失时 AddRoiCommand/RemoveRoiCommand 只会静默失败（FindByRoi 返回 null）。
            Assert.False(viewModel.UndoRedo.CanUndo);
            Assert.False(viewModel.UndoRedo.CanRedo);
        }

        [Fact]
        public void RegistryUnregister_MigratesStateThroughTheChangedEvent()
        {
            RoiPluginRegistry fullRegistry = RoiPluginRegistry.CreateBuiltIn();
            string linePluginTypeKey = fullRegistry.FindByType(typeof(LineMeasureRoi))!.TypeKey;
            var state = new ImageViewerPluginRuntimeState(fullRegistry, SelectedRoiDetectionService.Default);
            var line = new LineMeasureRoi();
            state.ViewModel.AddRoi(line);
            state.ViewModel.SelectedRoi = line;
            IReadOnlyList<RoiBase>? reported = null;
            IReadOnlyList<RoiPersistenceData>? payloads = null;
            state.RoiStateMigrated += migration =>
            {
                reported = migration.DroppedRois;
                payloads = migration.UnresolvedPayloads;
            };

            bool removed = fullRegistry.Unregister(fullRegistry.FindByRoi(line)!.TypeKey);

            Assert.True(removed);
            Assert.Equal([line], reported);
            Assert.Empty(state.ViewModel.AllRois);
            Assert.Null(state.ViewModel.SelectedRoi);
            Assert.Empty(state.ViewModel.GetRoiCollection<LineMeasureRoi>());

            // 关键：被移除的 ROI 必须序列化成 unresolved 载荷（用最后一次见过它的插件），装回插件后可还原。
            RoiPersistenceData payload = Assert.Single(payloads!);
            Assert.Equal(linePluginTypeKey, payload.Type);
            // 装回插件（全新注册表）后按稳定 TypeKey 还原，几何也一并恢复。
            var restoreRegistry = RoiPluginRegistry.CreateBuiltIn();
            var (restored, _, _, _) = RoiPersistenceService.CreateRois(
                new RoiDocument { Items = [payload] },
                restoreRegistry);
            var restoredLine = Assert.IsType<LineMeasureRoi>(Assert.Single(restored));
            Assert.Equal(line.P1, restoredLine.P1);
            Assert.Equal(line.P2, restoredLine.P2);
        }

        [Fact]
        public void RegistryRegister_MigratesStateThroughTheChangedEvent()
        {
            RoiPluginRegistry fullRegistry = RoiPluginRegistry.CreateBuiltIn();
            IRoiPlugin circlePlugin = fullRegistry.Plugins.Single(plugin => plugin.RoiType == typeof(CircleRoi));
            var registry = new RoiPluginRegistry();
            var state = new ImageViewerPluginRuntimeState(registry, SelectedRoiDetectionService.Default);
            var migrations = 0;
            IReadOnlyList<RoiBase>? reported = null;
            state.RoiStateMigrated += migration =>
            {
                migrations++;
                reported = migration.DroppedRois;
            };

            registry.Register(circlePlugin);

            // 就地注册也要走迁移（旧缓存集合必须被丢弃），并上报"没有被移除的 ROI"。
            Assert.Equal(1, migrations);
            Assert.Empty(reported!);

            // 注册之后新类型的 ROI 能正常进入按类型集合与总列表。
            var circle = new CircleRoi();
            state.ViewModel.AddRoi(circle);

            Assert.Same(circle, Assert.Single(state.ViewModel.AllRois));
            Assert.Same(circle, Assert.Single(state.ViewModel.GetRoiCollection<CircleRoi>()));
        }

        [Fact]
        public void RegistryBatchUpdate_CoalescesIntoASingleMigration()
        {
            RoiPluginRegistry fullRegistry = RoiPluginRegistry.CreateBuiltIn();
            var registry = new RoiPluginRegistry();
            var state = new ImageViewerPluginRuntimeState(registry, SelectedRoiDetectionService.Default);
            var migrations = 0;
            state.RoiStateMigrated += _ => migrations++;

            using (registry.BeginUpdate())
            {
                foreach (IRoiPlugin plugin in fullRegistry.Plugins)
                {
                    registry.Register(plugin);
                }
            }

            // 注册 N 个插件只产生一次迁移（模块加载路径已按模块合并）。
            Assert.Equal(1, migrations);
            Assert.Equal(fullRegistry.RegisteredTypeKeys.Count, registry.RegisteredTypeKeys.Count);
        }

        [Fact]
        public void Detach_StopsReactingToFurtherRegistryChanges()
        {
            RoiPluginRegistry registry = RoiPluginRegistry.CreateBuiltIn();
            var state = new ImageViewerPluginRuntimeState(registry, SelectedRoiDetectionService.Default);
            var line = new LineMeasureRoi();
            state.ViewModel.AddRoi(line);

            state.Detach();
            registry.Unregister(registry.FindByRoi(line)!.TypeKey);

            Assert.Same(line, Assert.Single(state.ViewModel.AllRois));
        }

        [Fact]
        public void PluginRegistryChanged_OnAnyThread_MigratesThroughTheScheduler()
        {
            // Changed 在调用方线程同步触发（后台热加载即后台线程），而迁移会修改 ObservableCollection
            // 并刷新 WPF 视觉——必须经调度器推迟到 UI 线程，而不是在触发线程上直接改。
            var scheduled = new List<Action>();
            RoiPluginRegistry registry = RoiPluginRegistry.CreateBuiltIn();
            var state = new ImageViewerPluginRuntimeState(
                registry,
                SelectedRoiDetectionService.Default,
                migrationScheduler: scheduled.Add);
            var line = new LineMeasureRoi();
            state.ViewModel.AddRoi(line);

            registry.Unregister(registry.FindByRoi(line)!.TypeKey);

            // 迁移已被推迟：触发线程上什么都没改。
            Assert.Single(scheduled);
            Assert.Single(state.ViewModel.AllRois);

            // 调度器执行（模拟回到 UI 线程）：迁移完成，ROI 进 unresolved 载荷。
            scheduled[0]();
            Assert.Empty(state.ViewModel.AllRois);
            Assert.Single(state.LastMigration!.UnresolvedPayloads);
        }

        [Fact]
        public void Detach_DiscardsQueuedMigrations()
        {
            var scheduled = new List<Action>();
            RoiPluginRegistry registry = RoiPluginRegistry.CreateBuiltIn();
            var state = new ImageViewerPluginRuntimeState(
                registry,
                SelectedRoiDetectionService.Default,
                migrationScheduler: action =>
                {
                    // 模拟 UI 线程繁忙：调度了但不立即执行。
                    scheduled.Add(action);
                });
            var line = new LineMeasureRoi();
            state.ViewModel.AddRoi(line);

            state.Detach();
            foreach (Action queued in scheduled.ToArray())
            {
                queued();
            }

            // Detach 之后排队的迁移必须被丢弃：disposed 控件不能再去改 ViewModel。
            Assert.Single(state.ViewModel.AllRois);
        }

        /// <summary>
        /// 用"排除某个 ROI 类型"的注册表模拟"新注册表不认识这类 ROI"。
        /// </summary>
        private static RoiPluginRegistry CreateRegistryWithout(RoiPluginRegistry source, System.Type excludedRoiType)
        {
            var registry = new RoiPluginRegistry();
            foreach (IRoiPlugin plugin in source.Plugins.Where(plugin => plugin.RoiType != excludedRoiType))
            {
                registry.Register(plugin);
            }

            return registry;
        }
    }
}
