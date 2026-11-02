# 模型资产库 P0 实施计划（立项排期）

配套设计：[model-asset-store-design.md](model-asset-store-design.md)。本文只覆盖 **P0**：
让 `match.shape` 能引用一个带版本的模型资产，并保留内联路径作为兼容与回滚。

## 一、范围

**P0 做**：SQLite 存储（3 张表）、资产/版本 API、`match.shape` 的 `modelAssetId`/`modelAssetVersion` 解析、
哈希校验、内联抽取为资产的命令、运行报告标注模型来源。

**P0 不做**（推 P1/P2）：配方发布清单锁定与启动校验、跨节点类型复用（`match.template` / `inference.*`）、
GC 与导入导出、签名/加密分发、前端完整资产浏览器（P0 只给工作区一个"保存为资产"动作）。

## 二、可复用的现有实现（调研结论）

| 现有资产 | 位置 | 复用方式 |
| --- | --- | --- |
| 版本化资产存储（337 行） | `VisionStudio.Api/CameraIntrinsicAssetStore.cs` | 照搬结构：`assets` + `versions` + `publication_history`，自带 `snapshot_hash` / `model_hash` / `created_by` |
| 版本化 DDL | `VisionStudio.Api/SchemaMigrations.cs`（当前 v23） | 追加 v24，三张 `CREATE TABLE IF NOT EXISTS`，`CurrentVersion` 改 24 |
| 薄端点封装（147 行） | `VisionStudio.Api/Endpoints/CameraEndpoints.cs` | 端点形状、权限特性、错误码直接对齐 |
| 模型缓存 | `VisionStudio.Engine/Algorithms/JLVisionModelCache.cs` | 运行时缓存键用 `sha256`，已支持 |
| 引擎侧宿主注入 | `VisionStudio.Engine/DeviceCommandOwnership.cs`（AsyncLocal） | 用同一模式注入只读的 `IModelAssetReader`，不让 Engine 依赖 Api |
| 模型包服务 | `VisionStudio.Api/ModelPackageService.cs` | **不混用**：它是文件系统 ONNX 包；模型资产是 SQLite 二进制，边界写进文档 |

## 三、任务分解

| 编号 | 任务 | 触及文件 | 验收 | 估算 |
| --- | --- | --- | --- | --- |
| **T1** | 存储层 | `SchemaMigrations.cs`（v24）、新增 `ShapeModelAssetStore.cs` | 建资产/追加版本/读版本/软删；同字节内容去重（`sha256` 唯一）；版本号并发冲突重试一次；单测 8 项 | 1.0 人日 |
| **T2** | 引擎解析 | 新增 `Engine/Runtime/ModelAssetAccess.cs`；`Algorithms/JLVisionMatching.cs`；`Batch5PrecisionCatalog.cs` 加 `modelAssetId`/`modelAssetVersion` | 有 id 时从 reader 取字节并校验 sha256；缺失/哈希不符明确失败；id 为空走内联；`diagnostics` 增加 `modelSource`/`modelSha256` | 1.0 人日 |
| **T3** | API 端点 | 新增 `Endpoints/ModelAssetEndpoints.cs`、`EndpointMapping.cs` 注册 | list / get / create / append-version / blob / soft-delete；权限与错误码对齐标定资产端点 | 0.5 人日 |
| **T4** | 抽取命令 | `ModelAssetEndpoints.cs` 增加 `POST /api/model-assets/extract`；前端 `useShapeMatchWorkspace.ts` 增加"保存为资产" | 从节点参数 `trainedModelData` 建资产并把节点参数切到 `modelAssetId`；文档体积下降 | 0.5 人日 |
| **T5** | 集成测试 | `Api.Tests/ModelAssetEndpointTests.cs`、`Engine.Tests/ShapeAssetResolutionTests.cs` | 两个节点共享同一资产；哈希不符拒跑；资产缺失不回退；开关关闭时行为与今天一致 | 0.5 人日 |
| **T6** | 观测与开关 | `appsettings.json`、运行报告序列化 | `modelAssets.enabled=false` 时 `modelAssetId` 被忽略（仅告警）；报告含 `modelSource`/`modelSha256` | 0.5 人日 |

合计 **4.0 人日**（不含代码评审、联调与现场验证）。

## 四、里程碑与顺序

| 里程碑 | 内容 | 可演示的结果 | 累计 |
| --- | --- | --- | --- |
| **M1** | T1 + T3 | 能通过 API 建资产、追加版本、取回字节；DB 升到 v24 | 1.5 人日 |
| **M2** | T2 | 节点填 `modelAssetId` 即可跑；内联路径不受影响 | 2.5 人日 |
| **M3** | T4 | 工作区一键把内联模型存为资产，节点参数切换、文档变小 | 3.0 人日 |
| **M4** | T5 + T6 | 负向用例与开关验证通过，可以交现场试用 | 4.0 人日 |

M1 与 M2 之间有硬依赖（存储格式定稿才能写解析）；T4 可并行做前端部分。

## 五、接口契约（P0 定稿）

```
GET    /api/model-assets?kind=shape                     列表
POST   /api/model-assets                                 { id?, name, kind, note?, modelBase64, trainingConfig, sourceImageSha256 }
GET    /api/model-assets/{id}                           资产 + 版本列表（不含字节）
POST   /api/model-assets/{id}/versions                   追加版本（同上，version 由服务端分配）
GET    /api/model-assets/{id}/versions/{v}/blob          字节流（application/octet-stream）
DELETE /api/model-assets/{id}                            软删；仍被节点引用时返回 409 + 引用清单
POST   /api/model-assets/extract                         { workflowId, nodeId } → { assetId, version }，并持久化节点参数变更
```

错误码约定：`404` 资产/版本不存在、`409` 版本冲突或仍有引用、`413` 超过 16 MB、`422` 哈希不符。

## 六、验收标准（P0 Exit Criteria）

- 典型 `match.shape` 节点：保存文档后模型字节不再进文档，节点参数体积 **< 10 KB**。
- 同一次训练的模型被两个节点引用，运行时 `modelSha256` 相同，`modelSource=asset`。
- 篡改资产字节或版本号 → 运行直接失败，错误信息含 `assetId`、期望哈希与实际哈希。
- 资产被删除或 id 不存在 → 失败，**不回退**内联模型。
- `modelAssets.enabled=false` → 行为与变更前逐字节一致（回归用例）。
- DB 从 v23 升到 v24 幂等；重复执行迁移不报错。

## 七、风险与处置

| 风险 | 处置 |
| --- | --- |
| 迁移失败阻塞启动 | v24 只加表，不改既有表；失败可回退代码，旧库仍可用 |
| 版本号并发冲突 | `(asset_id, version)` 唯一约束 + `MAX+1` 重试一次，仍冲突返回 409 |
| 16 MB 上限 | 与服务端现有 16 MB 模型上限一致，超限 `413`，不截断 |
| 引擎无 DI | 用现有 `AsyncLocal` 模式注入只读 reader；未注入时按"资产不可用"报明确错误 |
| 现场误用（资产未发布） | P0 不引入发布态，取"最新版本"；P1 才加发布锁定与回滚 |

## 八、已定的实现细节与唯一待确认项

**权限（已按标定资产的先例定稿）**：写操作沿用 `RequireEngineer(action, scope)`——
`model-asset.create` / `model-asset.version.create` / `model-asset.delete` / `model-asset.extract`，scope 用 `model-assets`；
读操作（list/get/blob）与 `/api/camera-intrinsics` 保持一致，不加额外策略。
审计动作名会随 `RequireEngineer` 进入安全审计，与标定资产的 `calibration.intrinsic-asset.*` 并列。

**待确认（影响 T4 是否进 M3）**：P0 的"保存为资产"入口放在训练工作区的保存按钮旁，
还是先只提供 API + 脚本跑通、前端放 P0.1？
倾向后者——后端闭环可以先验收，前端改动另开一个小批次，避免和现有训练工作区的交互耦合。

确认后按 M1 开始：先落 v24 迁移与 `ShapeModelAssetStore`，不动引擎行为，可独立评审。
