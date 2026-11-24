# 结果落库（output.database）

日期：2026-10-26 · 节点数 186 → 187 · 数据库 schema v22 → v23

## 为什么不做"节点直连数据库"

结果此前只能写文件（`output.exportResult`）或 HTTP 推走；要入库必须在上位机再写一套接收端。

但**引擎不该背数据库驱动**。做法是：

```
output.database  ──IResultRecordStore──▶  宿主实现（本仓库 = SQLite result_records 表）
```

- 引擎只依赖契约，换存储（SQLite → PostgreSQL → 时序库）不改配方、不改引擎。
- 节点不认识表结构，只声明"把这次结果追加到某个数据集"。

## 硬失败而不是静默丢弃

宿主没注册 `IResultRecordStore` 时，节点**直接失败**并给出替代方案（改用 `output.exportResult` 或启用宿主存储）。

理由：静默丢弃只会在事后对账时才暴露，那是产线最难查的一类问题。同理，写入失败会带上记录 id 与数据集名，
现场可以据此核对是否产生半行。

## 负载形态

与 `output.exportResult` 的 JSON **保持一致**，避免同一份数据在文件与库里两套结构：

```json
{"type":"NumberList","value":[1.5,2.5]}
```

图像/帧组降级成尺寸/相机摘要（不可能把像素写进库）。`maxPayloadBytes`（默认 64 KiB，上限 4 MiB）在写入前校验，
超限拒绝而不是截断落库。

## 数据集与业务键

| 参数 | 约束 | 用途 |
|---|---|---|
| `dataset` | 最长 96 字符，非空 | 逻辑分组：`caliper-width`、`ng-parts` |
| `key`（或 `key` 端口） | 最长 256 字符，空白归一为 null | 按件追溯（序列号/批次号） |
| `idempotencyKey`（或同名端口） | 最长 256 字符，空白表示每次追加 | 同一数据集内标识一次业务操作 |
| `idempotencyConflictPolicy` | `reuse`（默认）、`warn`、`fail` | 同一幂等键对应不同写入内容时的处理 |

端口优先于参数，与全仓既有约定一致。

同一次业务操作重试时复用幂等键，新操作使用新键。SQLite 对数据集和幂等键的组合做唯一约束，在单个事务内尝试写入并读取冲突记录。同一组合以首次提交为准，重试返回原记录的 `recordId`、`payloadBytes` 和 `reused=true`。业务键可以重复，可用于查询同一产品的多次检测。

### 幂等冲突策略

| 策略 | 同键时的行为 |
|---|---|
| `reuse` 复用首条记录 | 直接复用，沿用已有配方的默认行为 |
| `warn` 告警后复用 | 相同内容直接复用。内容有变化时返回首条记录，并输出 `idempotencyConflict=true` 和 `warningCount=1` |
| `fail` 冲突时报错 | 相同内容直接复用。内容有变化时节点报错，错误包含首条记录 ID |

`warn` 和 `fail` 比较规范化后的业务键与完整的 `{type,value}` 序列化文本。业务键去掉首尾空白，空白键按 null 比较。结果类型或 JSON 文本有变化都算冲突，运行 ID、节点 ID、工作流标识和写入时间不参与比较。图像以落库的尺寸摘要为比较依据。

幂等键为空时始终追加。成功输出 `reused` 表示返回首条记录，`idempotencyConflict=true` 表示 `warn` 策略明确检出冲突。每条冲突处理路径都保留首条记录。

## 存储层（宿主）

新增 migration 23，`CurrentVersion` 22 → 23：

```sql
CREATE TABLE result_records (
    id TEXT PRIMARY KEY, dataset TEXT NOT NULL, record_key TEXT NULL,
    payload_json TEXT NOT NULL, run_id TEXT NOT NULL, workflow_scope TEXT NULL,
    node_id TEXT NOT NULL, created_at TEXT NOT NULL
);
CREATE INDEX ix_result_records_dataset_created ON result_records(dataset, created_at DESC);
CREATE INDEX ix_result_records_key ON result_records(record_key);
```

迁移 34 增加可空的 `idempotency_key` 和 `(dataset, idempotency_key)` 唯一索引，已有记录保留。节点每次执行生成一个候选 GUID，业务重试由显式幂等键去重。报表仍可按数据集和业务键查询。

冲突检查使用同一个唯一索引，在同一写入事务内比较首条记录。自定义宿主需要实现 `AppendCheckedIdempotentAsync` 才能启用 `warn` 或 `fail`。

配套只读 API（报表/对账用，节点不调用）：`ReadAsync(dataset, limit)`、`ReadByKeyAsync(key, limit)`。

## 典型接线

```
value.statistics ──────────────┐
                               ├─ output.database(dataset="caliper-width", key 端口接条码)
flow.result.disposition ───────┘
```

```
inference.detection.detections ─ collection.detectionNumbers(confidence) ─ value.statistics ─ output.database(dataset="batch-confidence")
```

## 验证

| 侧 | 用例 | 覆盖点 |
|---|---|---|
| Engine | `AppendsRecordWithSerializedPayloadAndRunContext` | 负载 `{type,value}`、runId/scope/nodeId、输出端口 |
| Engine | `KeyPortOverridesParameterAndBlankKeyBecomesNull` | 端口优先、空白归一为 null |
| Engine | `ImageValueDegradesToSizeInsteadOfRawPixels` | 图像降级成尺寸 |
| Engine | `MissingStoreFailsInsteadOfSilentlyDroppingResults` | 无存储时拒绝执行并给替代方案 |
| Engine | `RejectsEmptyDatasetOversizedPayloadAndLongKey` | 三处校验，且不产生半条记录 |
| Engine | `StoreFailureIsWrappedWithRecordIdForReconciliation` | 失败信息带记录 id 与数据集 |
| Api | `MigrationCreatesResultRecordTable` | migration 23 建表、版本号 |
| Api | `AppendThenReadBackByDatasetAndKey` | 真实写入/按数据集/按业务键读取与排序 |
| Api | `DuplicateRecordIdIsRejectedSoRetriesCannotDoubleWrite` | 主键冲突拒绝重复写入 |

Engine 全量 1899、Api 441、前端 1122 全绿。

## 已知限制

- 只有**追加**语义：没有更新/删除/归档（对账与保留策略应由宿主的维护任务做）。
- 单条写入，没有批量事务：一个循环里 N 次 `output.database` 是 N 次事务。需要吞吐时应该攒成列表再写一次（当前节点只接受单值）。
- 没有查询节点：报表侧直接查库，避免把 SQL 塞进画布。
- `workflow_scope` 是编译计划标识，不是人可读的工作流名；按"哪条配方"统计需要宿主再关联。
