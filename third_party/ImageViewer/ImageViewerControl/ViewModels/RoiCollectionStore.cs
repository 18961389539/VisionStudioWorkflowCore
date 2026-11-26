using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using ImageViewer.Models;

namespace ImageViewer.ViewModels
{
    /// <summary>
    /// ROI 集合状态的唯一同步入口。
    /// Chinese: 类型集合是现有插件 API 的兼容投影，AllRois 由本类统一维护；批量更新不会把中间状态暴露给 UI。
    /// English: Typed collections remain compatibility projections for plugins, while this class owns AllRois
    /// synchronization and hides intermediate states during batch updates.
    /// </summary>
    internal sealed class RoiCollectionStore
    {
        private readonly Dictionary<Type, object> _typedCollections = new();
        private readonly ObservableCollection<RoiBase> _allRois = new();
        private readonly Func<IEnumerable<RoiBase>> _enumerateRois;
        private int _batchDepth;
        private bool _isSynchronizing;

        public RoiCollectionStore(Func<IEnumerable<RoiBase>> enumerateRois)
        {
            _enumerateRois = enumerateRois ?? throw new ArgumentNullException(nameof(enumerateRois));
        }

        public ObservableCollection<RoiBase> AllRois => _allRois;

        public ObservableCollection<T> Get<T>() where T : RoiBase
        {
            if (_typedCollections.TryGetValue(typeof(T), out object? existingCollection))
            {
                return (ObservableCollection<T>)existingCollection;
            }

            var collection = new ObservableCollection<T>();
            collection.CollectionChanged += OnTypedCollectionChanged;
            _typedCollections.Add(typeof(T), collection);
            return collection;
        }

        public void RunBatch(Action mutation)
        {
            ArgumentNullException.ThrowIfNull(mutation);

            _batchDepth++;
            try
            {
                mutation();
            }
            finally
            {
                _batchDepth--;
            }

            RebuildAllRois();
        }

        /// <summary>
        /// 丢弃全部按类型缓存的集合与总列表。
        /// Chinese: 供插件注册表变更使用。必须断开每个缓存集合的 CollectionChanged 订阅——否则仍然持有旧插件引用的
        /// 代码一旦改动这些集合，OnTypedCollectionChanged 就会把已经失效的 ROI 重新注入 AllRois
        /// （现象是"换了插件之后过一会儿旧标注又回来了"）。
        /// English: Drops every cached typed collection and the aggregate list. Unsubscribing matters: a stale reference
        /// that still mutates one of these collections would otherwise re-inject dropped ROIs into <see cref="AllRois"/>.
        /// </summary>
        public void Reset()
        {
            foreach (object collection in _typedCollections.Values)
            {
                DetachTypedCollection(collection);
            }

            _typedCollections.Clear();

            _isSynchronizing = true;
            try
            {
                _allRois.Clear();
            }
            finally
            {
                _isSynchronizing = false;
            }
        }

        private void DetachTypedCollection(object collection)
        {
            if (collection is INotifyCollectionChanged notifying)
            {
                notifying.CollectionChanged -= OnTypedCollectionChanged;
            }
        }

        public void RebuildAllRois(IEnumerable<RoiBase>? orderedRois = null)
        {
            _isSynchronizing = true;
            try
            {
                _allRois.Clear();
                foreach (RoiBase roi in orderedRois ?? _enumerateRois())
                {
                    _allRois.Add(roi);
                }
            }
            finally
            {
                _isSynchronizing = false;
            }
        }

        private void OnTypedCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_batchDepth > 0 || _isSynchronizing)
            {
                return;
            }

            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    AppendAllRois(e.NewItems);
                    break;
                case NotifyCollectionChangedAction.Remove:
                    RemoveAllRois(e.OldItems);
                    break;
                default:
                    RebuildAllRois();
                    break;
            }
        }

        private void AppendAllRois(IList? items)
        {
            if (items == null)
            {
                return;
            }

            _isSynchronizing = true;
            try
            {
                foreach (object? item in items)
                {
                    if (item is RoiBase roi && !_allRois.Contains(roi))
                    {
                        _allRois.Add(roi);
                    }
                }
            }
            finally
            {
                _isSynchronizing = false;
            }
        }

        private void RemoveAllRois(IList? items)
        {
            if (items == null)
            {
                return;
            }

            _isSynchronizing = true;
            try
            {
                foreach (object? item in items)
                {
                    if (item is RoiBase roi)
                    {
                        _allRois.Remove(roi);
                    }
                }
            }
            finally
            {
                _isSynchronizing = false;
            }
        }
    }
}
