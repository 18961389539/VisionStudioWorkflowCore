# 文档索引

按"我要干什么"分组，不按时间。带日期的文件是那一天的审计/整改记录，结论已落到代码与测试里，
不要在实现时把过期结论当现状——有冲突时以代码 + `scripts/static-audit.py` 的断言为准。

## 0. 先看这几份

| 文档 | 什么时候看 |
|---|---|
| [`../README.md`](../README.md) | 第一次进仓库：目录结构、本地开发、发布、现场配置要点 |
| [`quality-gates.md`](quality-gates.md) | 门禁红了 / 要加检查 / 想知道某个阈值怎么来的 |
| [`system-design-audit-2026-10-22.md`](system-design-audit-2026-10-22.md) | 要改宿主结构：分层、请求链路、失败语义 |
| [`improvement-backlog-2026-10-23.md`](improvement-backlog-2026-10-23.md) | 想找"下一步做什么"（含已落地与遗留项） |

## 1. 节点与流程能力

| 文档 | 内容 |
|---|---|
| [`node-maturity.md`](node-maturity.md) | Preview/Experimental 的分级判据、当前清单、**发布门禁** |
| [`node-design-improvements.md`](node-design-improvements.md) | 节点设计原则与改造记录 |
| [`core-node-design-corrections.md`](core-node-design-corrections.md) | 核心节点的语义更正 |
| [`node-execution-timeout.md`](node-execution-timeout.md) | 节点超时语义与中断 |
| [`general-loops.md`](general-loops.md) | 循环结构 |
| [`flow-log-and-fail.md`](flow-log-and-fail.md) | 执行日志与失败传递 |
| [`subworkflow-module-call.md`](subworkflow-module-call.md) | 模块复用（module.call） |
| [`list-types-and-statistics.md`](list-types-and-statistics.md) | 列表类型与统计口径 |
| [`value-string-node.md`](value-string-node.md) | 字符串/值节点 |
| [`threshold-segmentation.md`](threshold-segmentation.md) | 阈值分割 |
| [`mask-to-region-contour.md`](mask-to-region-contour.md) | 掩码 → 区域/轮廓（含 8 位截断口径） |
| [`measurement-gap-nodes.md`](measurement-gap-nodes.md) · [`region-gap-nodes.md`](region-gap-nodes.md) · [`preprocess-gap-nodes.md`](preprocess-gap-nodes.md) | 测量/区域/预处理的能力边界 |
| [`barcode-quality-node.md`](barcode-quality-node.md) | 条码质量（MOD 近似，不可出合规报告） |
| [`image-load-node.md`](image-load-node.md) · [`image-saving.md`](image-saving.md) | 图像载入/保存（含不支持的格式） |
| [`result-database-node.md`](result-database-node.md) | 结果入库（只追加、单条事务） |
| [`custom-protocol-nodes.md`](custom-protocol-nodes.md) | 串口/TCP/UDP 自定义协议节点 |
| [`jlvision-advanced-integration.md`](jlvision-advanced-integration.md) | JLVision 算法接入与 OpenCV 迁移边界 |
| [`hand-eye-calibration.md`](hand-eye-calibration.md) | 手眼标定 |
| [`model-asset-p0-plan.md`](model-asset-p0-plan.md) · [`model-asset-store-design.md`](model-asset-store-design.md) | 模型资产 |

## 2. 编辑器与操作台

| 文档 | 内容 |
|---|---|
| [`editor-document-design-improvements.md`](editor-document-design-improvements.md) | 文档模型、未保存变更、并发修订 |
| [`workflow-change-design-improvements.md`](workflow-change-design-improvements.md) | 变更语义与脏标记 |
| [`tuning-workspace-design-improvements.md`](tuning-workspace-design-improvements.md) | 调参工作区 |
| [`run-controller-design-improvements.md`](run-controller-design-improvements.md) | 运行控制与状态机 |
| [`trace-query-design-improvements.md`](trace-query-design-improvements.md) | 留证查询 |
| [`production-panel-design-improvements.md`](production-panel-design-improvements.md) | 生产面板 |
| [`recipe-publication-design-improvements.md`](recipe-publication-design-improvements.md) | 配方发布与回滚（含成熟度确认） |
| [`ui-ux-improvement-plan-2026-10-25.md`](ui-ux-improvement-plan-2026-10-25.md) | 交互与视觉计划 |
| [`product-concepts/2026-10-26/00-overview.html`](product-concepts/2026-10-26/00-overview.html) | 产品形态概念稿（HTML，浏览器打开） |

## 3. 审计、性能与工程治理

| 文档 | 内容 |
|---|---|
| [`design-audit-2026-10-26.md`](design-audit-2026-10-26.md) | 设计层复审 |
| [`perf-optimization-2026-10-26.md`](perf-optimization-2026-10-26.md) | 性能优化记录 |
| [`comment-code-audit-2026-10-29.md`](comment-code-audit-2026-10-29.md) | 注释与代码一致性审计 |
| [`quality-gates.md`](quality-gates.md) | 门禁口径与棘轮 |
| [`archive/node-design-improvements-rounds-1-51.md`](archive/node-design-improvements-rounds-1-51.md) | 早期 51 轮节点改造记录（历史） |

## 4. 运维、安全与可观测性

| 文档 | 内容 |
|---|---|
| [`diagnostic-bundle.md`](diagnostic-bundle.md) | 一键诊断包：打包什么、**刻意不打包什么**、日志尾部截断 |
| [`login-lockout.md`](login-lockout.md) | 登录失败计数与账号锁定（429 而非 401）、按 IP 限流与按账号锁定的分工 |
| [`engine-telemetry.md`](engine-telemetry.md) | 节点级 `ActivitySource` 遥测：标签口径、span 建在校验之后的原因 |
| [`frame-mat-pool.md`](frame-mat-pool.md) | 相机帧 `Mat` 复用池：归还点、容量上限、不清零的安全前提、`/api/health` 计数 |
| [`golden-regression.md`](golden-regression.md) | 流程级金样回归：固定 PNG 夹具、容差口径、重新生成后要改什么 |
| [`script-worker-isolation-plan.md`](script-worker-isolation-plan.md) | 脚本节点下沉 Worker 进程的改造方案（Phase 0/1/2/3 已实施）：进程隔离、Job Object 硬化、超时/内存/重启语义 |

## 5. 写文档的约定

- 日期落在文件名上：`*-audit-YYYY-MM-DD.md` 是一次性记录，结论要落到代码/测试，别让它成为唯一出处；
- 能力边界（"只支持子集""近似口径"）必须与 `NodeMaturityCatalog` 的定级一致，不一致时先改目录声明；
- 新增门禁检查时同步更新 `quality-gates.md`，否则下一个人只能在红叉面前猜。
