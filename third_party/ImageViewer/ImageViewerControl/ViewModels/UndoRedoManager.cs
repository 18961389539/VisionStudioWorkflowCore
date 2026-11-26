using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ImageViewer.ViewModels
{
    /// <summary>
    /// 撤销/重做命令接口
    /// Chinese: 定义撤销/重做命令所需的 Execute 与 Undo 方法。
    /// English: Interface for undo/redo commands exposing Execute and Undo methods.
    /// </summary>
    public interface IUndoRedoCommand
    {
        void Execute();
        void Undo();
    }

    /// <summary>
    /// 撤销/重做管理器
    /// Chinese: 记录执行的命令并支持 Undo/Redo 操作；撤销历史有深度上限，超限时淘汰最旧命令，
    /// 避免长时间标注导致内存无限增长。
    /// English: Tracks executed commands and supports Undo/Redo; undo history is depth-limited and
    /// evicts the oldest command when the limit is exceeded to bound memory growth.
    /// </summary>
    public class UndoRedoManager : INotifyPropertyChanged
    {
        /// <summary>默认撤销历史深度上限。</summary>
        public const int DefaultMaxDepth = 200;

        private readonly List<IUndoRedoCommand> _undoStack;
        private readonly Stack<IUndoRedoCommand> _redoStack = new Stack<IUndoRedoCommand>();

        public event PropertyChangedEventHandler? PropertyChanged;

        public UndoRedoManager(int maxDepth = DefaultMaxDepth)
        {
            MaxDepth = maxDepth > 0 ? maxDepth : DefaultMaxDepth;
            _undoStack = new List<IUndoRedoCommand>(MaxDepth);
        }

        /// <summary>撤销历史最大深度；超过后最旧的命令会被丢弃。</summary>
        public int MaxDepth { get; }

        /// <summary>
        /// 执行命令并将其推入撤销栈。
        /// Chinese: 执行给定的 IUndoRedoCommand，**成功之后**才将其推入撤销栈并清空重做栈；
        /// 若超出深度上限，淘汰最旧的命令。
        /// 失败语义：命令体抛异常时本管理器不做任何栈变更（命令不入栈、重做栈不清空），异常原样向上抛出。
        /// 这样"历史记录"始终与实际生效的编辑一致——不会出现"栈里记着已执行、实际没执行"的错位。
        /// English: Executes the command and only records it after it succeeds; a throwing command leaves the stacks
        /// untouched and the exception propagates, so bookkeeping never disagrees with the applied edits.
        /// </summary>
        /// <param name="command">要执行的命令 / Command to execute</param>
        public void Execute(IUndoRedoCommand command)
        {
            ArgumentNullException.ThrowIfNull(command);

            // 先执行、后记账：命令失败时撤销/重做栈保持原样。
            command.Execute();

            _undoStack.Add(command);
            if (_undoStack.Count > MaxDepth)
            {
                _undoStack.RemoveAt(0);
            }

            _redoStack.Clear();
            RaiseStateChanged();
        }

        /// <summary>
        /// 撤销上一个命令（如果存在）。
        /// Chinese: 调用栈顶命令的 Undo，**成功之后**才把它从撤销栈移到重做栈。
        /// 失败语义：Undo 抛异常时命令仍留在撤销栈（未移入重做栈），异常向上抛出；
        /// 因此 `CanUndo` 仍为 true，用户可重试，而不会出现"撤销失败却已被标记为可重做"的状态。
        /// English: Undoes the most recent command and only moves it to the redo stack on success;
        /// a throwing Undo leaves the command on the undo stack so the state stays retryable.
        /// </summary>
        public void Undo()
        {
            if (_undoStack.Count == 0)
            {
                return;
            }

            IUndoRedoCommand command = _undoStack[^1];
            command.Undo();
            _undoStack.RemoveAt(_undoStack.Count - 1);
            _redoStack.Push(command);
            RaiseStateChanged();
        }

        /// <summary>
        /// 重做上一个被撤销的命令（如果存在）。
        /// Chinese: 重新执行重做栈顶命令，**成功之后**才把它移回撤销栈。
        /// 失败语义：Execute 抛异常时命令仍留在重做栈（未移回撤销栈），异常向上抛出。
        /// English: Redoes the most recently undone command and only moves it back on success;
        /// a throwing Execute leaves it on the redo stack.
        /// </summary>
        public void Redo()
        {
            if (_redoStack.Count == 0)
            {
                return;
            }

            IUndoRedoCommand command = _redoStack.Peek();
            command.Execute();
            _redoStack.Pop();
            _undoStack.Add(command);
            if (_undoStack.Count > MaxDepth)
            {
                _undoStack.RemoveAt(0);
            }

            RaiseStateChanged();
        }

        /// <summary>
        /// 清空撤销与重做栈。
        /// Chinese: 移除所有记录的命令，重置历史。
        /// English: Clears both undo and redo stacks, resetting history.
        /// </summary>
        public void Clear()
        {
            _undoStack.Clear();
            _redoStack.Clear();
            RaiseStateChanged();
        }

        public bool CanUndo => _undoStack.Count > 0;
        public bool CanRedo => _redoStack.Count > 0;

        private void RaiseStateChanged()
        {
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
