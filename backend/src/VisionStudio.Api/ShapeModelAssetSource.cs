using Microsoft.Extensions.Configuration;
using VisionStudio.Abstractions;

namespace VisionStudio.Api;

/// <summary>
/// 把 <see cref="ShapeModelAssetStore"/> 适配成引擎侧的只读资产源（<see cref="IModelAssetSource"/>）。
///
/// 读不到就返回 null，由 match.shape 报"资产不存在/已删除"，绝不退回内联模型——
/// 静默换模型比失败危险得多。数据库异常不吞：让它以 5xx/运行失败暴露，而不是伪装成"资产缺失"。
/// </summary>
public sealed class ShapeModelAssetSource(ShapeModelAssetStore store, IConfiguration configuration) : IModelAssetSource
{
    /// <summary>运维回滚开关：appsettings 的 ModelAssets:Enabled，默认开启。</summary>
    public bool IsEnabled => configuration.GetValue("ModelAssets:Enabled", true);

    public byte[]? TryRead(string assetId, int version)
        => string.IsNullOrWhiteSpace(assetId) ? null : store.ReadBytesSync(assetId.Trim(), version);
}
