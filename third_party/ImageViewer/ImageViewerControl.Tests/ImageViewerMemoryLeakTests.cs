using System;
using System.Collections.Generic;
using System.Windows;
using ImageViewer.Controls;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.ViewModels;
using Xunit;

namespace ImageViewerControl.Tests
{
    [Collection(WpfTestCollection.Name)]
    [Trait("Category", "Leak")]
    [Trait("Category", "Wpf")]
    public class ImageViewerMemoryLeakTests
    {
        /// <summary>
        /// 控件 Dispose 后，且无外部强引用时，应能被 GC 完整回收（无事件/调度器/静态缓存泄漏）。
        /// </summary>
        [Fact]
        public void Dispose_ImageViewer_CanBeGarbageCollected()
        {
            WeakReference? reference = null;
            WpfTestRunner.Run(() =>
            {
                reference = CreateDisposedViewerWeakReference();
            });

            ForceGarbageCollection();

            Assert.False(reference!.IsAlive, "Dispose 后的 ImageViewer 仍被引用，存在内存泄漏。");
        }

        /// <summary>
        /// 仅作冒烟：作用域内的控件实例仍被强引用，弱引用 IsAlive 应当为 true，
        /// 验证弱引用测试机制本身不会误报（同 GC 测试搭配使用）。
        /// </summary>
        [Fact]
        public void AliveViewer_RemainsReachable_BeforeGc()
        {
            WpfTestRunner.Run(() =>
            {
                var viewer = new ImageViewer.Controls.ImageViewer();
                var reference = new WeakReference(viewer);
                Assert.True(reference.IsAlive, "作用域内强引用仍存活，弱引用 IsAlive 应为 true。");
            });
        }

        /// <summary>
        /// 撤销历史清空逻辑由命令层与会话/ROI 控制器测试覆盖（见 ImageViewerSessionControllerTests）；
        /// 此处验证 UndoRedoManager.Clear 会清空两个栈，作为长期运行内存有界性的底层保证。
        /// </summary>
        [Fact]
        public void UndoRedoManager_Clear_EmptiesStacks()
        {
            WpfTestRunner.Run(() =>
            {
                var manager = new UndoRedoManager();
                var viewModel = new ImageViewerViewModel(RoiPluginRegistry.CreateBuiltIn());
                manager.Execute(new AddRoiCommand(new LineMeasureRoi(), viewModel));
                Assert.True(manager.CanUndo);

                manager.Clear();

                Assert.False(manager.CanUndo);
                Assert.False(manager.CanRedo);
            });
        }

        [Fact]
        public void UndoRedoManager_ExceedingMaxDepth_EvictsOldestCommand()
        {
            WpfTestRunner.Run(() =>
            {
                var manager = new UndoRedoManager(maxDepth: 3);
                var viewModel = new ImageViewerViewModel(RoiPluginRegistry.CreateBuiltIn());
                var commands = new List<RoiBase>();

                for (int index = 0; index < 5; index++)
                {
                    var roi = new LineMeasureRoi();
                    commands.Add(roi);
                    manager.Execute(new AddRoiCommand(roi, viewModel));
                }

                // 最旧的两个命令应被淘汰，仅保留最近 3 个
                manager.Undo();
                Assert.DoesNotContain(commands[4], viewModel.AllRois);
                manager.Redo();

                for (int index = 0; index < 3; index++)
                {
                    manager.Undo();
                }

                Assert.False(manager.CanUndo);
                Assert.DoesNotContain(commands[2], viewModel.AllRois);
            });
        }

        /// <summary>
        /// 撤销命令执行抛异常时，命令必须留在撤销栈（而不是被当作"已撤销"移入重做栈），
        /// 否则会出现"撤销失败但 CanRedo 为 true"的状态错位；留在撤销栈后用户可重试。
        /// </summary>
        [Fact]
        public void UndoRedoManager_WhenUndoThrows_CommandRemainsInUndoStack()
        {
            WpfTestRunner.Run(() =>
            {
                var manager = new UndoRedoManager();
                var command = new ThrowingUndoCommand();
                manager.Execute(command);

                Assert.Throws<InvalidOperationException>(() => manager.Undo());

                Assert.True(manager.CanUndo, "撤销抛异常后命令应仍保留在撤销栈以便重试。");
                Assert.False(manager.CanRedo, "撤销未生效时不应把命令标记为可重做。");
            });
        }

        /// <summary>
        /// 重做命令执行抛异常时，命令必须留在重做栈（而不是被当作"已重做"移回撤销栈），
        /// 保持 CanRedo 为 true 供用户重试。
        /// </summary>
        [Fact]
        public void UndoRedoManager_WhenRedoThrows_CommandRemainsInRedoStack()
        {
            WpfTestRunner.Run(() =>
            {
                var manager = new UndoRedoManager();
                var command = new ThrowOnSecondExecuteCommand();
                manager.Execute(command);
                manager.Undo();

                Assert.Throws<InvalidOperationException>(() => manager.Redo());

                Assert.True(manager.CanRedo, "重做抛异常后命令应仍保留在重做栈以便重试。");
                Assert.False(manager.CanUndo, "重做未生效时不应把命令标记为可撤销。");
            });
        }

        private sealed class ThrowingUndoCommand : IUndoRedoCommand
        {
            public void Execute()
            {
            }

            public void Undo() => throw new InvalidOperationException("模拟撤销失败。");
        }

        private sealed class ThrowOnSecondExecuteCommand : IUndoRedoCommand
        {
            private int _executeCount;

            public void Execute()
            {
                _executeCount++;
                if (_executeCount == 2)
                {
                    throw new InvalidOperationException("模拟重做失败。");
                }
            }

            public void Undo()
            {
            }
        }

        private static WeakReference CreateDisposedViewerWeakReference()
        {
            var viewer = new ImageViewer.Controls.ImageViewer();
            viewer.Dispose();
            return new WeakReference(viewer);
        }

        private static WeakReference CreateAliveViewerWeakReference()
        {
            var viewer = new ImageViewer.Controls.ImageViewer();
            return new WeakReference(viewer);
        }

        private static void ForceGarbageCollection()
        {
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }
    }
}