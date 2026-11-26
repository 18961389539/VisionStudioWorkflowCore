#pragma warning disable CS1591
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ImageViewer.Common
{
    /// <summary>
    /// 基础视图模型，用于实现属性更改通知。
    /// Chinese: 提供常用的 INotifyPropertyChanged 实现与 SetProperty 辅助方法，供 ViewModel 与 Model 使用。
    /// English: Base ViewModel implementing INotifyPropertyChanged and a helper SetProperty method to simplify
    /// property setters.
    /// </summary>
    public class BaseViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 触发属性更改通知。
        /// Chinese: 调用 PropertyChanged 事件以通知绑定方属性已变更。
        /// English: Raises the PropertyChanged event to notify UI/data bindings that a property value changed.
        /// </summary>
        /// <param name="propertyName">属性名 / Name of the changed property</param>
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// 设置属性的值并在发生更改时通知绑定。
        /// Chinese: 如果新值与旧值不同，则更新字段并触发 OnPropertyChanged。
        /// English: Sets the backing field to the specified value and raises PropertyChanged when it changes.
        /// </summary>
        /// <typeparam name="T">属性类型 / Type of the property</typeparam>
        /// <param name="storage">引用到属性的后备字段 / Reference to the backing field</param>
        /// <param name="value">要设置的新值 / New value to set</param>
        /// <param name="propertyName">属性名（自动提供） / Property name (automatically provided)</param>
        /// <returns>如果值已更改则返回 true，否则返回 false / True if value changed, otherwise false.</returns>
        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(storage, value))
            {
                return false;
            }

            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
#pragma warning restore CS1591
