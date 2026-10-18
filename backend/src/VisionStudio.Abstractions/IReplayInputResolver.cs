namespace VisionStudio.Abstractions;

/// <summary>
/// 离线回放读盘授权契约。
/// <para>
/// <c>image.acquire</c> 的 <c>__replayInputPath</c> 只允许宿主（离线回放/数据集校验）注入，
/// 引擎不得信任工作流文档里自带的路径——否则一份外部导入的流程 JSON 就能读取本机任意文件。
/// 实现方负责把请求路径规范化，并仅在路径落在已授权根目录内时返回可读的绝对路径。
/// </para>
/// </summary>
public interface IReplayInputResolver
{
    /// <summary>返回已授权的绝对路径；不允许时返回 <c>null</c>（调用方必须拒绝执行）。</summary>
    string? Resolve(string requestedPath);
}
