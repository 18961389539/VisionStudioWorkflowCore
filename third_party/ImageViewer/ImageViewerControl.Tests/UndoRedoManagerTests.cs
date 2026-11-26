using System;
using System.Collections.Generic;
using ImageViewer.ViewModels;
using Xunit;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// <see cref="UndoRedoManager"/> 的直接单元测试。
    /// Chinese: 重点覆盖"命令抛异常时的栈状态一致性"——这是静态审查指出的薄弱点：
    /// 历史栈必须与真实生效的编辑保持一致，失败时不得把命令挪到对面栈。
    /// English: Direct tests for the undo/redo manager, focused on stack consistency when a command throws.
    /// </summary>
    public class UndoRedoManagerTests
    {
        [Fact]
        public void Execute_PushesCommandAndEnablesUndo()
        {
            var manager = new UndoRedoManager();
            var command = new RecordingCommand();

            manager.Execute(command);

            Assert.Equal("execute", Assert.Single(command.Log));
            Assert.True(manager.CanUndo);
            Assert.False(manager.CanRedo);
        }

        [Fact]
        public void Undo_MovesCommandToRedoStack()
        {
            var manager = new UndoRedoManager();
            var command = new RecordingCommand();
            manager.Execute(command);

            manager.Undo();

            Assert.Equal(["execute", "undo"], command.Log);
            Assert.False(manager.CanUndo);
            Assert.True(manager.CanRedo);
        }

        [Fact]
        public void Redo_ReExecutesAndMovesCommandBackToUndoStack()
        {
            var manager = new UndoRedoManager();
            var command = new RecordingCommand();
            manager.Execute(command);
            manager.Undo();

            manager.Redo();

            Assert.Equal(["execute", "undo", "execute"], command.Log);
            Assert.True(manager.CanUndo);
            Assert.False(manager.CanRedo);
        }

        [Fact]
        public void Execute_ClearsRedoStack()
        {
            var manager = new UndoRedoManager();
            manager.Execute(new RecordingCommand());
            manager.Undo();
            Assert.True(manager.CanRedo);

            manager.Execute(new RecordingCommand());

            Assert.False(manager.CanRedo);
        }

        [Fact]
        public void Execute_WhenCommandThrows_LeavesStacksUnchanged()
        {
            var manager = new UndoRedoManager();
            manager.Execute(new RecordingCommand());

            Assert.Throws<InvalidOperationException>(() => manager.Execute(new ThrowingOnExecuteCommand()));

            // 失败的命令既不入撤销栈，也不清空重做栈——历史仍与实际编辑一致。
            Assert.True(manager.CanUndo);
            Assert.False(manager.CanRedo);
        }

        [Fact]
        public void Execute_WhenCommandThrows_DoesNotClearExistingRedoStack()
        {
            var manager = new UndoRedoManager();
            manager.Execute(new RecordingCommand());
            manager.Undo();
            Assert.True(manager.CanRedo);

            Assert.Throws<InvalidOperationException>(() => manager.Execute(new ThrowingOnExecuteCommand()));

            Assert.True(manager.CanRedo, "失败的命令不应清空已有的重做历史。");
        }

        [Fact]
        public void Undo_WhenCommandThrows_KeepsCommandOnUndoStack()
        {
            var manager = new UndoRedoManager();
            manager.Execute(new ThrowingOnUndoCommand());

            Assert.Throws<InvalidOperationException>(() => manager.Undo());

            Assert.True(manager.CanUndo, "撤销失败后命令必须仍可撤销（可重试）。");
            Assert.False(manager.CanRedo, "撤销未生效时绝不能标记为可重做。");
        }

        [Fact]
        public void Undo_WhenCommandThrows_ThenRetrySucceeds()
        {
            var manager = new UndoRedoManager();
            var command = new FailFirstUndoCommand();
            manager.Execute(command);

            Assert.Throws<InvalidOperationException>(() => manager.Undo());
            manager.Undo(); // 重试

            Assert.False(manager.CanUndo);
            Assert.True(manager.CanRedo);
            Assert.Equal(2, command.UndoAttempts);
        }

        [Fact]
        public void Redo_WhenCommandThrows_KeepsCommandOnRedoStack()
        {
            var manager = new UndoRedoManager();
            var command = new FailSecondExecuteCommand();
            manager.Execute(command);
            manager.Undo();

            Assert.Throws<InvalidOperationException>(() => manager.Redo());

            Assert.True(manager.CanRedo, "重做失败后命令必须仍可重做（可重试）。");
            Assert.False(manager.CanUndo, "重做未生效时绝不能标记为可撤销。");
        }

        [Fact]
        public void DepthLimit_EvictsOldestCommand()
        {
            var manager = new UndoRedoManager(maxDepth: 2);
            manager.Execute(new RecordingCommand());
            manager.Execute(new RecordingCommand());
            manager.Execute(new RecordingCommand());

            // 只能撤两次，第三次撤销后 CanUndo 为 false（最旧的已被淘汰）。
            manager.Undo();
            manager.Undo();
            Assert.False(manager.CanUndo);
        }

        [Fact]
        public void Clear_EmptiesBothStacks()
        {
            var manager = new UndoRedoManager();
            manager.Execute(new RecordingCommand());
            manager.Undo();
            manager.Execute(new RecordingCommand());

            manager.Clear();

            Assert.False(manager.CanUndo);
            Assert.False(manager.CanRedo);
        }

        [Fact]
        public void StateChange_NotifiesPropertyChanged()
        {
            var manager = new UndoRedoManager();
            var changed = new List<string?>();
            manager.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

            manager.Execute(new RecordingCommand());

            Assert.Contains(nameof(UndoRedoManager.CanUndo), changed);
            Assert.Contains(nameof(UndoRedoManager.CanRedo), changed);
        }

        [Fact]
        public void Undo_WithEmptyStack_IsNoOp()
        {
            var manager = new UndoRedoManager();

            manager.Undo();

            Assert.False(manager.CanUndo);
            Assert.False(manager.CanRedo);
        }

        [Fact]
        public void Execute_WithNullCommand_Throws()
        {
            var manager = new UndoRedoManager();

            Assert.Throws<ArgumentNullException>(() => manager.Execute(null!));
        }

        private sealed class RecordingCommand : IUndoRedoCommand
        {
            public List<string> Log { get; } = [];

            public void Execute() => Log.Add("execute");

            public void Undo() => Log.Add("undo");
        }

        private sealed class ThrowingOnExecuteCommand : IUndoRedoCommand
        {
            public void Execute() => throw new InvalidOperationException("模拟执行失败。");

            public void Undo()
            {
            }
        }

        private sealed class ThrowingOnUndoCommand : IUndoRedoCommand
        {
            public void Execute()
            {
            }

            public void Undo() => throw new InvalidOperationException("模拟撤销失败。");
        }

        private sealed class FailFirstUndoCommand : IUndoRedoCommand
        {
            public int UndoAttempts { get; private set; }

            public void Execute()
            {
            }

            public void Undo()
            {
                UndoAttempts++;
                if (UndoAttempts == 1)
                {
                    throw new InvalidOperationException("模拟首次撤销失败。");
                }
            }
        }

        private sealed class FailSecondExecuteCommand : IUndoRedoCommand
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
    }
}
