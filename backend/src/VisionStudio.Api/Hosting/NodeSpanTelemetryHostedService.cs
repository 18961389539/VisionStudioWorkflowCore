using VisionStudio.Engine.Diagnostics;

namespace VisionStudio.Api.Hosting;

/// <summary>
/// 启动即实例化 <see cref="NodeSpanBuffer"/>（2026-11-12）。
///
/// 为什么需要一个空壳 HostedService：DI 单例是**惰性**的——没人解析就不构造，
/// 而 <see cref="NodeSpanBuffer"/> 的构造函数正是"注册 ActivityListener"的地方。
/// 如果只写 <c>services.AddSingleton&lt;NodeSpanBuffer&gt;()</c>，第一次解析发生在
/// 有人调用 /api/diagnostics/node-spans 的时候，此前的全部 span 都会被静默错过。
///
/// 这个坑不是理论问题：真后端冒烟（frontend/e2e/real-backend-smoke.spec.ts）第一次跑
/// 就抓到了 `captured = 0` —— 流程已经跑完、追溯已经落库，而 span 一条都没有。
/// 因此监听器的生命周期必须绑在宿主启动，而不是绑在"第一次有人想看"。
/// </summary>
internal sealed class NodeSpanTelemetryHostedService(NodeSpanBuffer buffer) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 构造已经完成（监听器在构造函数里注册）；这里只留一条启动线索，
        // 让日志能回答"这次启动到底有没有开遥测"。
        _ = buffer.Snapshot(0).Capacity;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
