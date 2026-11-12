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

字段不改变**执行语义**：Preview / Experimental 节点照样能加进流程、能跑。但自 2026-11-12 起，
它们不能**静默发布**——见下节「发布门禁」。

## 发布门禁（2026-11-12）

此前成熟度只做 UI 披露，工程师把 `inference.obb` 加进流程后点"发布"没有任何阻力；发布之后
这条信息也消失了（配方管理只显示"已验证 / 已发布"）。数据集验证记录衡量的是准确率，不能替代
"这个节点的算法路径从未被断言"。

现在的口径：

| 时机 | 行为 |
|---|---|
| 加入画布 / 配置节点 | 不变（徽标 + 悬停理由） |
| 发布配方版本（`POST /api/jobs/{id}/versions/{v}/publish`） | 工作流含非 Stable 节点时返回 **409 + `NODE_MATURITY_ACK_REQUIRED`**，响应体列出 `findings`（节点 id/类型/名称/级别/理由） |
| 显式确认 | 追加 `?acknowledgeMaturityNodes=true` 重新提交；确认结果写入审计 `target`（`v{version} maturity:experimental=N,preview=M;types=...`），发布历史可回查 |
| 回滚 | **不设门禁**：目标版本当初发布时已确认过，回滚是恢复既有结论，不是引入新风险 |

前端在发布对话框里把门禁渲染成"可继续的一步"：列出节点与理由 + 必勾选确认，而不是退化成
"配方或发布条件已变化，请重新核对"——后者只会训练出"看到红字就再点一次"的习惯。

已知边界：`module.call` 引用的模块内部节点不在当前工作流的 `Nodes` 里，门禁不递归展开。模块自身
发布走同一条 publish 路径，因此模块发布时已覆盖；门禁保证的是"这份工作流里写着的节点"。

守卫：`NodeMaturityGateTests`（扫描/排序/审计摘要 + 无确认拒绝、有确认放行、Stable 不受影响）、
`recipePublicationDialog.test.tsx`（门禁面板与勾选后才可提交、普通 409 不被误判为门禁）。

## 当前清单

**Experimental（3）**——三个推理节点没有可用的模型包，端到端推理与后处理串联未验证；
解码、旋转 IoU、NMS、掩码合成等纯函数已有数值测试，但节点级 NMS→输出、关键点配对与骨架
仍未被断言：

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
