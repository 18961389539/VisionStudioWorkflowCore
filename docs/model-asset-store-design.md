# 模型资产库设计草案（训练一次、多工位复用）

状态：**设计草案，未实现**。关联：`match.shape` 的训练/运行两阶段、配方发布、工作流文档体积。

## 背景：现状是"模型住在节点里"

`match.shape` 的训练流程已经存在，只是模型没有独立身份：

| 事实 | 位置 |
| --- | --- |
| 训练由调优工作区带外触发（`trainingMode=true`） | `Algorithms/JLVisionMatching.cs`、`useShapeMatchWorkspace.ts` |
| 训练结果以 **base64** 写回节点参数 `trainedModelData`（≤16 MB） | 同上 |
| 训练参数指纹存 `trainedModelConfig`，不一致时拒绝匹配并要求重训 | 同上 |
| 运行期反序列化后按 SHA256 进 `JLVisionModelCache`（容量 16） | `JLVisionModelCache.cs` |

由此产生的实际影响：

| 问题 | 影响 |
| --- | --- |
| 模型随工作流文档保存 | 一个形状节点让文档涨到数 MB～十几 MB；复制/版本管理/接口传输都被拖慢 |
| 模型没有独立身份 | 同一零件在 3 个工位各训一次，无法证明"用的是同一个模型" |
| 无法版本化 | 重训即覆盖，旧版本不可回滚；A/B 只能靠复制节点 |
| 无审计 | 说不清谁在什么时候用什么样本训的、上线的是哪一版 |
| 无权限隔离 | 模型随文档走，文档的读写权限就等于模型权限 |

## 目标 / 非目标

**目标**：模型与流程解耦；内容寻址 + 版本化；发布时锁定、运行时可校验；多工位复用同一资产；文档体积回到 KB 级；训练过程可追溯。

**非目标（本设计不解决）**：跨平台模型格式转换、模型加密分发、训练算法本身、在线增量学习。

## 数据模型

```
model_asset(id, project_id, name, kind='shape', labels_json, created_at, created_by)
model_asset_version(asset_id, version, sha256, size_bytes,
                    training_config_json,    -- 即 trainedModelConfig 的内容
                    source_image_sha256,     -- 训练源图哈希，用于追溯
                    note, created_at, created_by)
model_blob(sha256 PRIMARY KEY, bytes BLOB)   -- 内容寻址，天然去重
```

- `kind` 预留 `shape` / `template` / `feature` / `onnx`，P2 再扩展。
- 版本不可变：重训产生新 `version`，旧版本保留。
- 同一 `sha256` 只存一份字节；版本行引用它。

## 节点契约变化（以 `match.shape` 为样板）

| 参数/输出 | 语义 |
| --- | --- |
| `modelAssetId`（新，text，默认空） | 空 = 沿用内联 `trainedModelData`（现有行为，完全兼容）；非空 = 从资产库解析 |
| `modelAssetVersion`（新，number，默认 0） | 0 = 取该资产**当前发布版本**；发布配方时把解析结果固定成具体版本号 |
| `modelSource`（输出/诊断） | `inline` / `asset`，便于现场判断用的是哪条路径 |
| `modelSha256`（输出/诊断） | 运行时实际加载的字节哈希；与发布清单不一致直接失败 |

解析优先级与失败语义：

1. `modelAssetId` 非空 → 读资产版本 → 校验 `sha256` → 反序列化；
2. 资产缺失 / 哈希不符 / 版本不存在 → **明确报错**，绝不静默回退到内联模型（静默回退等于偷偷换模型）；
3. `modelAssetId` 为空 → 内联路径，行为与今天一致。

## 与配方发布的关系

- 发布配方时，把每个引用写成清单条目：`{ nodeId, assetId, version, sha256 }`；
- 运行时按清单校验：任意一项不符即拒绝启动并报出节点与资产；
- 回滚配方 = 切回旧清单，不需要重训；
- 未发布草稿默认允许 `modelAssetVersion=0`（跟随最新发布版），发布动作把它固化成具体版本。

## API 草案

```
POST   /api/model-assets                     创建资产（含首个版本）
POST   /api/model-assets/{id}/versions       追加版本
GET    /api/model-assets?kind=shape          列表（名称、版本数、最新版本、大小）
GET    /api/model-assets/{id}/versions/{v}   元数据
GET    /api/model-assets/{id}/versions/{v}/blob   字节流（按权限）
DELETE /api/model-assets/{id}                软删除（存在引用时返回 409 + 引用清单）
```

训练返回里增加 `trainedModelSha256`，工作区"保存为资产"直接调 `POST /api/model-assets`；
保存成功后把节点参数从内联切成 `modelAssetId/Version`，文档体积立刻下降。

## 迁移

1. 存量节点：提供"抽取内联模型为资产"命令（批量可选）。动作只有两件事——上传字节、替换参数；
   模型字节不变，因此**可逆**（资产删掉、参数换回 base64 即可回退）。
2. 迁移不阻塞：不迁移的节点继续走内联路径，与现在完全一致。
3. 抽取后 `trainedModelConfig` 保留在节点上，便于比对"资产里的模型 vs 当前参数"是否已过期。

## 存储、并发与回收

- 与现有存储一致用 SQLite：`model_blob` 存 BLOB，沿用 16 MB/版本上限；超限报错而不是截断。
- 写入：同一事务内 `INSERT OR IGNORE model_blob` + `INSERT model_asset_version`，`(asset_id, version)` 唯一约束；
  版本号用 `MAX(version)+1` 并在冲突时重试一次。
- 读取：流式返回，运行期只读；缓存键用 `sha256`（与现有 `JLVisionModelCache` 对齐）。
- 回收：未被任何节点参数/配方清单引用、且超过保留期的版本可清理；默认只标记不删除，需人工确认。

## 分阶段实施

| 阶段 | 范围 | 验收 |
| --- | --- | --- |
| **P0** | 表结构 + 创建/读取 API + `match.shape` 的 `modelAssetId` + 抽取命令 | 文档体积下降；两个节点引用同一资产；哈希校验生效 |
| **P1** | 版本列表/回滚 + 发布清单锁定 + 校验失败拒跑 | 切回旧版本无需重训；篡改哈希即拒绝启动 |
| **P2** | 复用给 `match.template` / `match.feature` / `inference.*`；GC；`.vsasset` 导入导出 | 一次训练多类节点复用；跨项目导出可复现 |
| **P3** | 签名/加密分发、跨站点同步 | 按需 |

## 风险与回滚

| 风险 | 处置 |
| --- | --- |
| 资产库损坏或丢失 | P0 不删除内联字段；随时可退回内联路径 |
| 版本漂移（资产被追加新版本） | 发布清单固定 `version + sha256`，运行期强校验 |
| 迁移误操作 | 迁移只改参数字段，模型字节不动，可逐节点回退 |
| 权限收紧导致运行失败 | 运行期只需读权限；权限不足报明确错误码，不回退 |

## 验收指标

- 典型 `match.shape` 节点文档体积：数 MB → **< 10 KB**（不含资产本体）。
- 同一零件 N 个工位：训练次数 1，模型哈希一致。
- 任意一次运行可回指 `(assetId, version, sha256, trainingConfig, sourceImageSha256)`。
- 回滚一次模型：0 次重训、1 次配方版本切换。
