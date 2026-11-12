using System.Net;
// ForwardedHeadersOptions 自 .NET 10 起位于 Microsoft.AspNetCore.Builder（策略类型），
// ForwardedHeaders 枚举仍在 Microsoft.AspNetCore.HttpOverrides。
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using VisionStudio.Api.Hosting;
using Xunit;

namespace VisionStudio.Api.Tests;

/// <summary>
/// 2026-11-12 整改回归：反向代理边界。
///
/// 背景：限流按 <c>Connection.RemoteIpAddress</c> 分区、会话 Cookie 的 Secure 取
/// <c>Request.IsHttps</c>。部署在 TLS 终止代理之后若不解析 X-Forwarded-*，全厂会共享
/// 10 次/分钟登录配额（一人输错密码锁死所有人），同时 Secure 失效、审计来源 IP 全是代理地址。
///
/// 这组用例锁住的是"配置到选项"的映射与默认值语义——默认必须**关闭**（直连部署时开启等于
/// 允许任何客户端伪造来源 IP 绕过限流），显式声明可信代理后必须**只**信任声明的范围。
/// </summary>
public sealed class ReverseProxyTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(x => new KeyValuePair<string, string?>(x.Key, x.Value)))
            .Build();

    private static ForwardedHeadersOptions Options(params (string Key, string Value)[] values)
    {
        var options = new ForwardedHeadersOptions();
        ReverseProxyRegistration.Apply(options, Config(values));
        return options;
    }

    [Fact]
    public void DisabledByDefault_LeavesHeadersUnparsed()
    {
        // 不配任何东西 = 直连部署，必须一个头都不解析。
        var options = Options();
        Assert.Equal(ForwardedHeaders.None, options.ForwardedHeaders);
    }

    [Fact]
    public void Enabled_ParsesClientAddressAndScheme()
    {
        var options = Options(("ReverseProxy:Enabled", "true"));
        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
        // Host 头不默认解析：解析 Host 会把 Host 注入面扩大到代理层。
        Assert.False(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedHost));
    }

    [Fact]
    public void Enabled_DefaultsToOneHopAndLoopbackProxy()
    {
        var options = Options(("ReverseProxy:Enabled", "true"));
        // 只解析一跳：客户端可自带伪造的 X-Forwarded-For，多解析一跳就把伪造 IP 当真实来源。
        Assert.Equal(1, options.ForwardLimit);
        // 未声明可信代理时沿用框架默认（仅 loopback），即"代理与本进程同机"。
        Assert.True(
            options.KnownProxies.Contains(IPAddress.Loopback) ||
            options.KnownIPNetworks.Any(network => network.Contains(IPAddress.Loopback)),
            $"默认可信范围应含 loopback；proxies=[{string.Join(",", options.KnownProxies)}] " +
            $"networks=[{string.Join(",", options.KnownIPNetworks)}]");
    }

    [Fact]
    public void ForwardHost_OptIn_AddsHostHeader()
    {
        var options = Options(("ReverseProxy:Enabled", "true"), ("ReverseProxy:ForwardHost", "true"));
        Assert.True(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedHost));
    }

    [Fact]
    public void ExplicitProxies_ReplaceLoopbackDefault()
    {
        var options = Options(
            ("ReverseProxy:Enabled", "true"),
            ("ReverseProxy:KnownProxies:0", "10.0.0.5"),
            ("ReverseProxy:KnownProxies:1", "::1"));

        // 声明了可信代理就只信任声明过的：默认的 127.0.0.1 必须被清掉，
        // 否则"声明了 A 却还信任 B"会让可信边界说不清。
        Assert.Equal(2, options.KnownProxies.Count);
        Assert.Contains(IPAddress.Parse("10.0.0.5"), options.KnownProxies);
        Assert.DoesNotContain(IPAddress.Loopback, options.KnownProxies);
    }

    [Fact]
    public void ExplicitNetworks_ReplaceLoopbackDefault()
    {
        var options = Options(
            ("ReverseProxy:Enabled", "true"),
            ("ReverseProxy:KnownNetworks:0", "10.0.0.0/8"));

        Assert.Single(options.KnownIPNetworks);
        Assert.True(options.KnownIPNetworks[0].Contains(IPAddress.Parse("10.1.2.3")));
        Assert.False(options.KnownIPNetworks[0].Contains(IPAddress.Parse("11.1.2.3")));
        Assert.Empty(options.KnownProxies);
    }

    [Fact]
    public void InvalidProxy_FailsFastWithActionableMessage()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Options(
            ("ReverseProxy:Enabled", "true"),
            ("ReverseProxy:KnownProxies:0", "proxy.local")));

        // 启动即失败好过"配了没生效"：现场排查一个静默失效的可信代理要花掉整班时间。
        Assert.Contains("proxy.local", error.Message);
        Assert.Contains("KnownNetworks", error.Message);
    }

    [Fact]
    public void InvalidNetwork_FailsFastWithCidrHint()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Options(
            ("ReverseProxy:Enabled", "true"),
            ("ReverseProxy:KnownNetworks:0", "10.0.0.0")));

        Assert.Contains("10.0.0.0", error.Message);
        Assert.Contains("CIDR", error.Message);
    }

    [Fact]
    public void Describe_ReportsEffectivePolicyForStartupBanner()
    {
        Assert.Contains("off", ReverseProxyRegistration.Describe(Config()));
        Assert.Contains("loopback-only", ReverseProxyRegistration.Describe(Config(("ReverseProxy:Enabled", "true"))));
        Assert.Contains("1 proxies", ReverseProxyRegistration.Describe(Config(
            ("ReverseProxy:Enabled", "true"),
            ("ReverseProxy:KnownProxies:0", "10.0.0.5"))));
    }
}
