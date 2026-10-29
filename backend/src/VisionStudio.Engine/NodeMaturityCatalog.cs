using VisionStudio.Abstractions;

namespace VisionStudio.Engine;

/// <summary>
/// 节点成熟度声明（2026-10-29 审计落地）。
///
/// 为什么集中在一处而不是写进 34 个目录文件：
/// 成熟度是"当前验证状态"的审计结论，不是节点自身的算法语义——它会随补测试/补文档升降级，
/// 每次变动都应该是可 review 的一次改表，而不是散落在各目录文件里的一行属性。
/// 目录条目仍然是节点能力的唯一真相源，本表只做覆盖（Stable 不写进目录，也不进 JSON）。
///
/// 定级依据只有两类可核对的证据，不接受主观判断：
/// 1. 文档里写明的适用边界（docs/*.md 的"已知限制"章节、目录长描述里的 single-target 之类）；
/// 2. 行为测试是否真的跑了算法——只有"缺模型/参数校验"这类错误路径测试的，不能算验证过。
///
/// 防漂移守卫见 NodeMaturityTests：表里的 type 必须存在于目录（改名后不会静默失效）、
/// 每条必须给理由、分级集合是棘轮（升降级必须改测试里的期望表）。
///
/// 分级口径见 <see cref="NodeMaturity"/>：Stable = 有行为基线且文档无保留；
/// Preview = 有行为基线但文档写明适用范围；Experimental = 没有算法级行为基线。
///
/// 2026-10-29 更正：首轮审计用"测试源码里是否出现节点类型字符串"统计覆盖，
/// 把大量用执行器类名构造的用例误判为"零测试"（match.feature / contour.fromMask /
/// jlvision.* / measure.fitRectangle / calibration.detectBoard 实际都有数值断言）。
/// 本表已按"执行器是否被实例化 + 断言是否为数值/几何"重核，误判条目全部回到 Stable。
/// </summary>
public static class NodeMaturityCatalog
{
    /// <summary>一条成熟度声明：级别 + 为什么（写给下一个维护者看，不进 API）。</summary>
    public sealed record Declaration(NodeMaturity Maturity, string Reason);

    /// <summary>非 Stable 节点的声明表；未列出的节点一律 Stable。</summary>
    public static IReadOnlyDictionary<string, Declaration> Declarations { get; } =
        new Dictionary<string, Declaration>(StringComparer.OrdinalIgnoreCase)
        {
            // ---------------------------------------------------------- Experimental
            // 判据：算法路径没有行为基线。下面三个推理节点只有"拒绝裁剪 / 缺模型报错"的契约测试，
            // 仓库里没有可用的模型包，推理精度与后处理（NMS/掩码解码/关键点配对）从未被断言。

            ["inference.instanceSegmentation"] = new(NodeMaturity.Experimental,
                "实例分割只有契约与错误路径测试，无模型包可跑：掩码解码与实例后处理未做数值验证。"),

            ["inference.obb"] = new(NodeMaturity.Experimental,
                "旋转框检测只有契约与错误路径测试，无模型包可跑：角度解码与 NMS 未做数值验证。"),

            ["inference.pose"] = new(NodeMaturity.Experimental,
                "姿态估计只有契约与错误路径测试，无模型包可跑：关键点配对与置信度未做数值验证。"),

            // ---------------------------------------------------------- Preview
            // 判据：有行为基线，但文档（docs/*.md 或目录长描述）写明了适用范围/近似口径。

            ["contour.fromMask"] = new(NodeMaturity.Preview,
                "与 region.fromMask 共用阈值语义：掩码统一转 8 位，0–1 浮点软掩码会被截成 0/255（docs/mask-to-region-contour.md）。"),

            ["device.serialPort"] = new(NodeMaturity.Preview,
                "CI 无串口硬件：只有配置校验与协议帧测试，真实波特率/流控需现场验证（docs/custom-protocol-nodes.md）。"),

            ["device.tcpRequest"] = new(NodeMaturity.Preview,
                "不做长连接/重连与 TLS，只有单次请求-响应语义（docs/custom-protocol-nodes.md）。"),

            ["device.udpMessage"] = new(NodeMaturity.Preview,
                "无重传与分片重组，丢包由业务侧承担（docs/custom-protocol-nodes.md）。"),

            ["image.load"] = new(NodeMaturity.Preview,
                "不支持多页 TIFF 页选择、RAW/DNG、视频帧；目录枚举每次重新列目录（docs/image-load-node.md）。"),

            ["match.feature"] = new(NodeMaturity.Preview,
                "目录长描述自述 single-target：只取首个一致变换，不做旋转训练的多目标搜索（多目标用 match.shape）。"),

            ["measure.gear"] = new(NodeMaturity.Preview,
                "纯算量节点（无图像输入）：只做直齿轮，螺旋角按 0，变位系数限 ±2（docs/measurement-gap-nodes.md）。"),

            ["measure.profileFilter"] = new(NodeMaturity.Preview,
                "ISO 16610-21 高斯滤波要求输入是已采样且名义上直的轮廓；曲面先给名义线，否则形状误差会混进波纹度。"),

            ["measure.roughness"] = new(NodeMaturity.Preview,
                "影像法 Ra/Rq/Rz 只当趋势，不替代触针；输入轮廓必须来自直线边的采样（docs/measurement-gap-nodes.md）。"),

            ["measure.thread"] = new(NodeMaturity.Preview,
                "轮廓法（silhouette）看不到中径牙根，pitchDiameter 只是 (大径+小径)/2 的估计（docs/measurement-gap-nodes.md）。"),

            ["output.database"] = new(NodeMaturity.Preview,
                "只追加、单条事务、无查询/更新/删除；批量吞吐要靠上游攒列表（docs/result-database-node.md）。"),

            ["recognition.barcodeQuality"] = new(NodeMaturity.Preview,
                "MOD 是模块均值偏离中值的近似，只有 SC/MOD/AN，缺固定图形损伤，不能用于出具合规报告（docs/barcode-quality-node.md）。"),

            ["region.fromMask"] = new(NodeMaturity.Preview,
                "掩码统一转 8 位：0–1 浮点软掩码会被截成 0/255；组件数沿用 VisionRegion 的 10000 上限（docs/mask-to-region-contour.md）。"),

            ["tolerance.stackup"] = new(NodeMaturity.Preview,
                "RSS 按各环独立 ±3σ 假设，不处理相关环；增减环靠 sign 显式给出（docs/measurement-gap-nodes.md）。"),

            ["value.counter"] = new(NodeMaturity.Preview,
                "跨轮计数有行为基线；状态由宿主 IRunCounterStore 承载并每 5s 快照到 data/runtime/counters.json，窗口内停机最多丢 5s 增量。")
        };

    /// <summary>按类型取声明；未声明即 Stable。</summary>
    public static Declaration? Find(string type)
        => Declarations.TryGetValue(type, out var declaration) ? declaration : null;

    /// <summary>把声明覆盖到目录条目上（Stable 保持默认值，不写入 JSON）。</summary>
    public static NodeCatalogItem Apply(NodeCatalogItem item)
        => Find(item.Type) is { } declaration ? item with { Maturity = declaration.Maturity } : item;
}
