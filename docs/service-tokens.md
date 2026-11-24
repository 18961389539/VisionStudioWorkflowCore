# 服务账号令牌（MES / 上位机）

服务令牌用于让 MES、上位机、线边系统以**独立身份**接入，而不是共用某个人类操作员账号。
共用账号无法回答"这次运行是谁触发的"，也无法在 MES 被换掉时单独收回权限。

实现：`Security/ServiceTokenStore.cs`、`Security/VisionStudioAuthenticationHandler.cs`、
`Endpoints/SecurityEndpoints.cs`；迁移 v32 建表 `security_service_tokens`。

## 令牌格式与存储

```
vst_<43 字符 base64url>        例：vst_9mQ...（共 47 字符）
```

- 明文只出现在**创建**和**轮换**的响应里一次，之后任何列表接口都不返回。
- 库内只存 `token_hash = SHA-256(明文)` 的十六进制串；`prefix`（前 12 字符）用于在界面/日志里辨认是哪一枚。
- 认证时对请求携带的明文做 SHA-256 后按 `token_hash` 查找；令牌内容不落日志。
- 名称唯一且不区分大小写，审计里的 `service:<名称>` 因此没有歧义。

## 作用域

| scope | 允许的接口 | 等价人类角色 |
|---|---|---|
| `results:read` | `GET /api/traces`、`/api/traces/page`、`/api/traces/stats`、`/api/traces/{runId}` | Operator 及以上 |
| `stations:read` | `GET /api/production/stations/local` | Operator 及以上 |
| `stations:deliver` | `POST /api/production/recipe-deliveries` | 使用工位独立接收令牌 |
| `stations:control` | `POST /api/production/start`、`POST /api/production/stop` | Operator 及以上 |
| `runs:run` | `POST /api/run`（触发一次临时流程运行） | Engineer 及以上 |

规则：

- 至少一个作用域；未知作用域直接 400。
- 认证成功后角色固定为 `Service`，**不在** `SecurityRoles.All` 里，不能作为用户角色分配。
- 全局兜底策略（"已登录即可"）显式排除服务主体：没有标注作用域的接口一律 403。
  这条是默认拒绝的核心——否则一个只读 MES 令牌能读诊断、资产、配置等所有未标注角色的接口。
- 人类角色策略（Operator/Engineer/Administrator）天然不包含 `Service`，所以管理类接口也到不了。

## 管理接口（仅 Administrator）

```bash
# 创建：响应里的 token 只出现这一次
curl -X POST https://host/api/security/service-tokens \
  -H 'Content-Type: application/json' --cookie 'vs_session=...' \
  -d '{"name":"mes-line1","scopes":["results:read"],"expiresInDays":180}'
# => {"token":"vst_...","details":{"id":"...","prefix":"vst_AbCdEfGh",...}}

# 列表：只有前缀、作用域、创建/过期/最近使用/吊销时间
curl https://host/api/security/service-tokens --cookie 'vs_session=...'

# 轮换：同 id/名称/作用域/过期时间，换新密钥；旧密钥立刻 401
curl -X POST https://host/api/security/service-tokens/{id}/rotate --cookie 'vs_session=...'

# 吊销：幂等；已吊销令牌不能轮换（避免"吊销后又复活"）
curl -X POST https://host/api/security/service-tokens/{id}/revoke --cookie 'vs_session=...'
```

创建、轮换、吊销都进审计（`security.service-token.*`，target 为名称或 id）。
`last_used_at` 在认证成功时按令牌每分钟最多写一次，用于识别长期未用的令牌。

## 使用方式

```bash
curl https://host/api/traces/page?limit=20 -H "Authorization: Bearer vst_..."
```

- 只走 `Authorization: Bearer`；cookie 只承载人类会话，两者不混用。
- 过期（可选 `expiresInDays`，1–3650 天）、吊销后认证直接 401；无作用域 403。
- 每次带 `results:read` 的读请求会写一条 `trace.read` 审计（`username=service:<名称>`、
  `role=Service`）。人类轮询同一接口不写，避免把审计表变成访问日志。
- 触发运行始终写 `workflow.run.adhoc` 审计，与人类 Engineer 同口径。
- `stations:control` 令牌只允许启停该工位现有生产配置，不能读取配置、切换配方或调用恢复接口。

## 部署建议

- MES 侧把明文放在自己的密钥管理里；轮换时先发新令牌、切换、再吊销旧的。
- 现场必须走 HTTPS（或可信反向代理终止 TLS）：Bearer 头与口令同属明文凭据。
- 定期检查列表中的 `lastUsedAt` 与 `expiresAt`，清理不再使用的令牌。
- 令牌不绑定 IP/主机：网络层隔离仍由防火墙/网段负责。

## 回归测试

`backend/tests/VisionStudio.Api.Tests/Operations/ServiceTokenTests.cs`：

- 库内只有哈希、响应只回一次明文；
- `results:read` 能读追溯但不能读目录/用户/触发运行，且读操作进审计；
- `runs:run` 能触发运行但不能读追溯；
- 轮换使旧密钥 401、吊销使新密钥 401、已吊销不可轮换；
- Operator 不能管理令牌；未知作用域 400。