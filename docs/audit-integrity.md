# 审计防篡改：哈希链与校验端点

`audit_events` 此前只是一张普通表：没有删除接口，但任何能打开 `visionstudio.db` 的人
都可以直接 `UPDATE`/`DELETE`，事后无法判断哪些行被改过。v33 起每行带上
`prev_hash`/`hash`，把审计变成**可发现局部篡改**的哈希链。

实现：`Security/AuditChain.cs`、`Security/Audit.cs`、迁移 v33（`SchemaMigrations.Integration.cs`）。

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
- 追加在 `BEGIN IMMEDIATE` 事务内完成：读最后一行 hash → 取 `MAX(seq)+1` → 计算 → 带显式 seq 插入。
  并发审计不会分叉成两条同 seq 的链。
- 迁移 v33 在事务内按 seq 回填旧行；列扩展走 `EnsureColumnAsync`，恢复旧备份重放迁移是安全的。

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