# 节点成熟度（Preview / Experimental）

日期：2026-10-29 · 目录 242 条不变

## 为什么加这个字段

目录此前对所有节点一视同仁：`NodeCatalogItem` 里有能力位（ROI/Overlay/确定性）、动作声明、坐标帧语义，
但没有任何"验证到什么程度"的披露。工程师在工具箱里看到的 `measure.thread`（轮廓法，测不出中径牙根）
和 `image.crop`（有几何断言的行为基线）长得完全一样，只能靠翻文档或现场试错。

所以新增 `NodeMaturity`：

| 级别 | 含义 | 判据 |
|---|---|---|
| `Stable`（默认） | 可以按文档直接用 | 有覆盖算法的行为测试，且文档没有适用性保留 |
| `Preview` | 可用，但有明确边界 | 有行为测试，但文档写明近似口径 / 只支持子集 / 不能用于合规判定 |
| `Experimental` | 上线前自行验证 | 算法路径没有行为基线（只有契约测试、错误路径测试，或依赖外部模型/硬件） |

字段只做**披露**，不改变执行语义：Preview / Experimental 节点照样能加进流程、能跑、能上产线，
只是界面会标出来。

## 当前清单

**Experimental（3）**——三个推理节点只有"拒绝 crop / 缺模型报错"这类错误路径测试，
仓库里没有可用的模型包，推理精度与后处理（NMS、掩码解码、关键点配对）从未被断言：

`inference.obb`、`inference.pose`、`inference.instanceSegmentation`

**Preview（15）**——每条的理由都写在 `NodeMaturityCatalog.cs` 里，可对回文档原文：

`contour.fromMask`、`region.fromMask`（软掩码截断 8 位，组件数上限）、
`device.serialPort`、`device.tcpRequest`、`device.udpMessage`（无长连接/TLS/重传，串口无硬件级测试）、
`image.load`（无多页 TIFF/RAW）、`match.feature`（single-target）、
`measure.gear`（只直齿）、`measure.profileFilter`、`measure.roughness`（只当趋势）、
`measure.thread`（silhouette，中径是估计）、`tolerance.stackup`（RSS ±3σ 独立假设）、
`recognition.barcodeQuality`（MOD 近似，不能出合规报告）、
`output.database`（只追加、单条事务）、`value.counter`（5s 快照窗口）

## 界面

- 工具箱条目右侧、节点配置对话框标题：`预览` / `实验` 徽标，悬停给出原因（不是重复标签）；
- `Stable` 不画徽标，也不写进目录 JSON（`JsonIgnore(WhenWritingDefault)`）——
  其余 224 个节点的序列化形状与哈希不受影响；
- 后端枚举以 PascalCase 字符串下发（全局 `JsonStringEnumConverter`），前端 `nodeMaturity.ts` 负责归一化，
  **字段缺失必须按 Stable 处理**。

## 升降级怎么改

1. 改 `backend/src/VisionStudio.Engine/NodeMaturityCatalog.cs`（一条声明 = 级别 + 理由）；
2. 同步 `NodeMaturityTests.ExpectedPreview/ExpectedExperimental` 的棘轮表（否则测试失败，这是有意的）；
3. 重新生成前端快照：`bash scripts/generate-frontend-catalog.sh`；
4. 前端 `src/__tests__/nodeMaturity.test.ts` 的计数断言会提示你同步。

守卫覆盖：声明表的 type 必须在目录中存在（改名后不会静默失效）、每条必须给理由、
分级集合是棘轮、目录 JSON 只为非 Stable 写字段、快照往返保留级别。

## 附：首轮审计的误判与更正

首轮统计用"测试源码里是否出现节点类型字符串"来判断覆盖，得出"9 个节点零行为测试"。
实际核对发现多数用例是用**执行器类名**构造的（`new FeatureMatchNode()`、`new CalibrationDetectBoardNode()`），
`match.feature`（旋转/缩放/角度断言）、`contour.fromMask`、`jlvision.*`、`measure.fitRectangle`
都有数值级断言，误判条目已全部回到 Stable。

结论：判断覆盖要看"执行器是否被实例化 + 断言是否落在数值/几何上"，
不能数类型字符串——这也是 `NodeMaturityCatalog` 定级只认两类证据的原因。
