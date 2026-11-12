using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace VisionStudio.Api.Hosting;

/// <summary>
/// 反向代理边界（2026-11-12 整改）。
///
/// 具体问题：会话限流按 <c>Connection.RemoteIpAddress</c> 分区（auth：10 次/分钟），
/// 会话 Cookie 的 <c>Secure</c> 取自 <c>Request.IsHttps</c>。两者读的都是"直连对端"，一旦前面
/// 挂了 Nginx / IIS ARR / 云负载均衡做 TLS 终止，就会出现三个真实故障：
///   1. 所有客户端在 Kestrel 眼里是同一个代理 IP → 全厂共享 10 次/分钟登录配额，
///      一个人连续输错密码就能把所有人锁在门外（这是可用性事故，不是安全加固）；
///   2. <c>Request.IsHttps=false</c> → 会话 Cookie 丢掉 Secure 标志，可被明文请求带走；
///   3. 审计日志与限流统计里的来源 IP 全是代理地址，事后追责失去依据。
///
/// 因此部署在代理之后必须解析 X-Forwarded-For / X-Forwarded-Proto。**默认关闭**：直连部署时
/// 开启反而危险——任何人都能自带 X-Forwarded-For 冒充任意 IP，绕过限流并伪造审计来源。
///
/// 开启后只信任显式声明的 <c>ReverseProxy:KnownProxies</c> / <c>KnownNetworks</c>；未声明时
/// 沿用框架默认（仅 loopback），即"代理与本进程同机"这一最常见形态。可信代理之外的请求即使
/// 携带 X-Forwarded-* 也会被忽略。
/// </summary>
public static class ReverseProxyRegistration
{
    public const string SectionName = "ReverseProxy";

    /// <summary>绑定 <c>ReverseProxy</c> 节到 <see cref="ForwardedHeadersOptions"/>。</summary>
    public static IServiceCollection AddVisionStudioForwardedHeaders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ForwardedHeadersOptions>(options => Apply(options, configuration));
        return services;
    }

    /// <summary>配置反转结果；internal 以便测试直接断言选项，不必起真实宿主。</summary>
    internal static void Apply(ForwardedHeadersOptions options, IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        if (!(section.GetValue<bool?>("Enabled") ?? false))
        {
            // 关闭时显式置空：框架默认值不是 None，留着会让 UseForwardedHeaders 变成
            // "什么都不解析"的空转中间件，反而让人以为已经配好了。
            options.ForwardedHeaders = ForwardedHeaders.None;
            return;
        }

        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        // 只有明确要按原始 Host 生成重定向/绝对链接时才解析 X-Forwarded-Host：
        // 解析 Host 会把 Host 头注入面扩大到代理层。
        if (section.GetValue<bool?>("ForwardHost") ?? false)
            options.ForwardedHeaders |= ForwardedHeaders.XForwardedHost;

        // 只解析最后 N 跳，默认 1。客户端可以在自己的那一跳塞任意 X-Forwarded-For，
        // 多解析一跳就等于把伪造 IP 当成真实来源。
        options.ForwardLimit = Math.Max(1, section.GetValue<int?>("ForwardLimit") ?? 1);

        var proxies = section.GetSection("KnownProxies").Get<string[]>() ?? [];
        var networks = section.GetSection("KnownNetworks").Get<string[]>() ?? [];
        if (proxies.Length == 0 && networks.Length == 0) return;

        // 一旦显式声明可信范围，就清掉框架默认的 loopback，避免"声明了 A 却还信任 B"。
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var proxy in proxies)
        {
            if (!IPAddress.TryParse(proxy, out var address))
                throw new InvalidOperationException(
                    $"ReverseProxy:KnownProxies 中的 '{proxy}' 不是合法 IP 地址。" +
                    "请填写代理出口地址（如 10.0.0.5 或 ::1）；网段请改用 ReverseProxy:KnownNetworks。");
            options.KnownProxies.Add(address);
        }

        foreach (var network in networks)
        {
            // 显式限定 System.Net.IPNetwork：Microsoft.AspNetCore.HttpOverrides.IPNetwork 同名，
            // 而 KnownIPNetworks（.NET 10 起替代已过时的 KnownNetworks）用的是 System.Net 版本。
            if (!System.Net.IPNetwork.TryParse(network, out var parsed))
                throw new InvalidOperationException(
                    $"ReverseProxy:KnownNetworks 中的 '{network}' 不是合法网段。" +
                    "请使用 CIDR 形式（如 10.0.0.0/8、fd00::/8）。");
            options.KnownIPNetworks.Add(parsed);
        }
    }

    /// <summary>
    /// 启动横幅用的一句话摘要：把"实际生效的反代策略"写进日志。
    /// 现场最怕的是配了没生效——部署在代理后面却仍按代理 IP 限流，出问题时日志里必须先能看出来。
    /// </summary>
    internal static string Describe(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        if (!(section.GetValue<bool?>("Enabled") ?? false)) return "off (直连部署)";
        var proxies = section.GetSection("KnownProxies").Get<string[]>() ?? [];
        var networks = section.GetSection("KnownNetworks").Get<string[]>() ?? [];
        var trusted = proxies.Length == 0 && networks.Length == 0
            ? "loopback-only"
            : $"{proxies.Length} proxies / {networks.Length} networks";
        var limit = Math.Max(1, section.GetValue<int?>("ForwardLimit") ?? 1);
        return $"on (limit={limit}, trusted={trusted})";
    }
}
