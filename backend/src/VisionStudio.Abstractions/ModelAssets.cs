namespace VisionStudio.Abstractions;

/// <summary>
/// 模型资产读取端口（P0）。宿主（Api）实现，Engine 只声明契约——避免 Engine 反向依赖 Api。
///
/// 刻意是**同步**接口：字节来自本地 SQLite，节点执行路径已经很长，不该为了读一次 BLOB
/// 把 async 传染到匹配算法里。实现方负责"读不到"的语义（返回 null），由节点给出明确错误，
/// 绝不静默回退到别处的模型。
/// </summary>
public interface IModelAssetSource
{
    /// <summary>读取指定资产版本的模型字节；资产/版本不存在或已删除时返回 null。</summary>
    byte[]? TryRead(string assetId, int version);
}
