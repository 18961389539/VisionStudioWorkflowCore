using System.Diagnostics;

namespace VisionStudio.Api.Infrastructure;

/// <summary>
/// 请求级关联 ID 的唯一来源：首次读取时计算一次（优先 W3C trace id，回退 <see cref="HttpContext.TraceIdentifier"/>）
/// 并缓存到 <see cref="HttpContext.Items"/>。审计记录、异常响应与日志、维护/运行时限制响应、认证挑战
/// 因此永远使用同一个标识——响应里的 correlationId 一定能匹配到审计记录。
/// </summary>
public static class RequestCorrelation
{
    private const string ItemKey = "VisionStudio.CorrelationId";

    public static string Get(HttpContext context)
    {
        if (context.Items.TryGetValue(ItemKey, out var cached) && cached is string cachedValue)
            return cachedValue;

        var id = Activity.Current?.Id ?? context.TraceIdentifier;
        context.Items[ItemKey] = id;
        return id;
    }
}
