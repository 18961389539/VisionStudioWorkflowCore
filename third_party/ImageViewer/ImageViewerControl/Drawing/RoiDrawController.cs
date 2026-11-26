using System;
using System.Windows.Input;

namespace ImageViewer.Drawing
{
    /// <summary>
    /// 通用的 <see cref="IRoiDrawController"/> 实现：绑定一个光标与会话工厂。
    /// Chinese: 让内置工具在声明式工厂里用少量代码声明绘制行为，无需为每个工具建类。
    /// English: Generic controller binding a cursor to a session factory, so built-in tools can be
    /// declared concisely in the declarative factory without one class per tool.
    /// </summary>
    public sealed class RoiDrawController : IRoiDrawController
    {
        private readonly Func<IRoiDrawSession> _sessionFactory;

        public RoiDrawController(Cursor cursor, Func<IRoiDrawSession> sessionFactory)
        {
            Cursor = cursor ?? throw new ArgumentNullException(nameof(cursor));
            _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        }

        public Cursor Cursor { get; }

        public IRoiDrawSession CreateSession() => _sessionFactory();
    }
}
