# 审计防篡改：哈希链与校验端点

`audit_events` 此前只是一张普通表：没有删除接口，但任何能打开 `visionstudio.db` 的人
都可以直接 `UPDATE`/`DELETE`，事后无法判断哪些行被改过。v33 起每行带上
`prev_hash`/`hash`，把审计变成**可发现局部篡改**的哈希链。

实现：`Security/AuditChain.cs`、`Security/Audit.cs`、`Security/AuditEventStore.Writer.cs`、
迁移 v33（`SchemaMigrations.Integration.cs`）。

## 链怎么算

```
hash(n) = SHA-256( JSON 数组 [
    prev_hash(n), seq, at, user_id, username, role,
    action, resource, method, path, status_code,
    success(1/0), target, correlation_id, client_ip
] )
prev_hash(n) = hash(n-1)；首行 prev_hash = 64 个 0（GenesisHash）
```

- 字段顺序即协议：调整顺序或增删字段会让所有历史行校验失败，等同让历史审计作废。
- 追加由单消费者按入队顺序批量执行：每批在一个 `BEGIN IMMEDIATE` 事务内读一次最后一行 hash，
  从 `MAX(seq)+1` 连续计算各行 hash 并带显式 seq 插入，整批提交。链尾不跨批缓存；
  独立 Store 实例的追加仍由 SQLite 事务串行化，不会分叉。
- 迁移 v33 在事务内按 seq 回填旧行；列扩展走 `EnsureColumnAsync`，恢复旧备份重放迁移是安全的。

## 单写者批量队列

`Security:AuditQueue` 默认 `Capacity=1024`、`BatchSize=64`、`FlushIntervalMs=5`、
`DrainTimeoutMs=15000`。合法范围依次为 1–65536、1–1024、1–1000、1–120000；
非法配置会拒绝启动。队列满时等待，不丢弃、不覆盖旧记录；默认每批最多 64 条。
低流量时在数据库事务外等待短暂聚合窗口，高流量时直接取满批，避免每个请求都争抢一次写锁。
SQLite 与产线追溯仍共用一个写锁：这里减少的是写事务数和竞争者，不保证消除磁盘故障或所有写入等待。

- 入队前复制身份、路径、target、关联 ID 和时间，不把 `HttpContext` 留给后台线程。
- `RecordAsync` **提交成功后才完成**，不是 fire-and-forget。取消只影响尚未入队的调用；
  已接收记录即使请求断开也会等待提交结果。串行单请求仍可能每次单独提交。
- 每批失败会回滚，所有受影响调用得到异常；不自动重放提交结果不明的事务，避免重复审计。
  中间件仍保持主请求结果不被审计异常替换，并记录结构化错误日志；失败按记录计入
  `/api/health.audit.persistFailures`。后续批次重新读取链尾后可继续写入，失败计数不会自动清零。
- 接收期间持有独立维护租约，直到提交或明确失败。恢复维护须等这些租约排空；
  维护期间拒绝新入队，恢复自身的审计可在退出排他窗口、进入待重启状态后写入。
- 宿主正常停止会关闭入口并排空已接收记录；超过停机预算会报错、尝试取消剩余数据库操作，
  不把超时当作成功。`shutdownFailures` 单独计数，不重复计入失败记录数。
- `/api/health.audit` 另暴露 `pendingEvents`（含等待队列空间的调用）、`committedBatches`、
  `persistedEvents`。队列在内存中，进程强杀/断电仍可能损失尚未提交的记录；
  成功提交的断电耐久性仍遵循现有 SQLite WAL/NORMAL 设置，本次不改变持久化等级。

## 校验

```
GET /api/security/audit/verify        # 仅 Administrator，校验动作本身也进审计
```

响应：

```json
{
  "valid": true,
  "checked": 1421,
  "firstInvalidSeq": null,
  "firstInvalidReason": null,
  "lastSeq": 1421,
  "lastHash": "…",
  "verifiedAt": "2026-11-23T09:30:00+00:00"
}
```

按 seq 升序逐行做三件事：

1. 行内 `prev_hash` 必须等于上一行的 `hash`（缺失列 → `missing-hash`，链接断开 → `link-mismatch`）；
2. 用行内字段重算 `hash`，必须与库内一致（`hash-mismatch`）；
3. 返回**第一个**不可信行的 seq，便于人工从那里开始核查。

校验是只读的：不自动修复。"能自动修复的校验"等于没有校验。

## 能发现什么

- `UPDATE` 任意行字段（含时间、用户、动作、结果）→ 本行 `hash-mismatch`；
- 删除中间行 → 下一行 `link-mismatch`；
- 插入伪造行 → 本行或下一行链接/哈希不匹配；
- 把 `hash` 列清空或改成随手值 → `missing-hash` / `hash-mismatch`。

## 不能发现什么（必须知道）

- **整体重算链**：能改库又能跑脚本的人可以按规范重算所有 hash，让校验重新变"valid"。
  要发现这类改写，需要库外锚点——定期备份快照、把 `lastSeq/lastHash` 抄到外部日志/看板，
  或对备份文件做哈希留存。审计链只能降低门槛，不能替代外部存证。
- **删除尾部行**：链是追加式的，删掉最后几行不会破坏剩余链接。定期记录 `lastSeq` 是对策。
- **清空整表**：会退化成"全新库"（空链 valid）。同理需要外部锚点。
- 校验端点本身依赖本机时钟与管理会话；它不是远程证明（remote attestation）。

## 使用建议

- 关键产线可让上位机/巡检任务每天调一次 `/api/security/audit/verify`，把 `lastSeq/lastHash`
  写入外部系统；两次记录之间若 `lastSeq` 回退或 `lastHash` 对不上，说明库被替换。
- 升级后首次启动会自动回填历史行；回填耗时与审计行数成正比，发生在迁移事务内。
- 备份/恢复流程无需改动：恢复后启动会重放迁移，链保持一致；旧备份（无 hash 列）会被回填。

## 回归测试

`backend/tests/VisionStudio.Api.Tests/Operations/AuditChainTests.cs`：

- 连续写入形成有效链；
- 修改中间行 → `hash-mismatch` 且 seq 正确；
- 删除中间行 → 下一行 `link-mismatch`；
- `/api/security/audit/verify` 仅 Administrator 可读，且能发现篡改。

`Operations/AuditQueueTests.cs` 额外验证：128 条并发审计在批大小 16 时最多使用 9 个写事务；
满队列反压、入队前/后取消、停机排空与超时、整批回滚、请求快照、跨消费者链尾重读，
以及恢复维护等待和恢复后待重启状态下的审计。