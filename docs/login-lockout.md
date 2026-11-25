# 登录失败计数与账号锁定

没有这一层时，`POST /api/security/login` 只有一个**按 IP 的限流**（`auth` 策略，10 次/分钟）。
限流挡不住口令爆破：攻击者换个 IP 继续试，或者干脆放慢到 9 次/分钟。现场的实际风险更朴素——
工位机上有人反复猜口令，而系统只在日志里留一串 401，没有任何人会发现。

- 存储：`backend/src/VisionStudio.Api/Security/SecurityStore.cs`（`LoginAsync` / `UnlockAsync`）
- 选项：`SecurityOptions.MaxFailedLoginAttempts`（默认 5）、`LockoutMinutes`（默认 15）
- 迁移：schema v28，`security_users.failed_attempts` + `security_users.locked_until`
- 解锁：`POST /api/security/users/{id}/unlock`（Administrator，审计 `security.user.unlock`）
- 用例：`SecurityTests.RepeatedFailedLogins_LockTheAccount_UntilAnAdministratorUnlocksIt`

## 两道闸的分工

| 闸 | 粒度 | 默认值 | 挡什么 |
|---|---|---|---|
| 限流 `auth` | 按客户端 IP（走 `X-Forwarded-For`，见 `ReverseProxyRegistration`） | 10 次/分钟 | 单机暴力尝试、脚本刷接口 |
| 锁定 | **按账号** | 5 次失败 / 15 分钟 | 换 IP 的爆破、慢速猜测、内部人员乱试 |

只有 IP 限流时，换 IP 就绕过了；只有账号锁定时，攻击者可以用大量账号把所有人锁住（这是一种
拒绝服务，所以锁定窗口必须短，且管理员能一键解开）。

## 为什么锁定返回 429 而不是 401

401 的含义是"你的凭据不对"，前端会把它渲染成"用户名或密码错误"。账号被锁时前端还这么提示，
用户唯一会做的动作就是**继续重试**，而每次重试都在延长痛苦。所以：

- 401 `unauthorized`：口令错，还能再试；
- 429 `account_locked`：别再试了，等窗口过去或找管理员。

`ApiTooManyRequestsException` 里有同样的注释。响应文案带明确的解锁时间（UTC），
现场人员照着念就能判断"该等还是该叫人"。

## 计数与窗口的语义

| 情况 | 行为 |
|---|---|
| 输错一次 | `failed_attempts += 1`；达到上限时**同一条 UPDATE** 写入 `locked_until` |
| 锁定窗口内登录 | 直接 429，**不验证口令**（否则正确口令能在锁定期偷跑，锁定就形同虚设） |
| 窗口已过 | 先清零计数与窗口，再验证口令 |
| 登录成功 | 清零（只有真的登录成功才清，不因"账号存在"清） |
| 管理员重置口令 / 用户改口令 | 一并清零——重置本就是为了让人重新进来 |
| 账号被禁用 | 不计数：禁用账号无论如何进不来，给它攒计数只会制造无意义的锁 |

「先清零再验证」这一步容易漏：如果窗口过期后不清零，用户下一次输错时 `attempts` 从 5 变 6，
立刻又被锁 15 分钟——表现是"明明过了 15 分钟还是进不去"。

计数与锁定写在同一行同一条语句里，避免"计数加了、锁定没写"的半状态。高并发下最多多记一次
失败（`row.FailedAttempts + 1` 是读后写），不会绕过上限。

## 运维动作

```bash
# 看谁被锁了（管理员令牌）
curl -H "Cookie: vs_session=..." http://localhost:5000/api/security/users

# 解锁（审计里留 security.user.unlock + target=用户 id）
curl -X POST -H "Cookie: vs_session=..." http://localhost:5000/api/security/users/<id>/unlock
```

`MaxFailedLoginAttempts = 0` 关闭整套机制（用于不支持锁定的联调环境）。这是显式开关，
不是"调大上限"——调大上限仍然会锁，只是晚一点。

## Cookie 会话的写请求来源校验

`SameSite=Strict` 的 site 不区分端口，不能独自阻止同主机另一端口发起的写操作。
认证后的 Cookie 请求除 GET、HEAD、OPTIONS、TRACE 外统一经过 `CookieCsrfMiddleware`，
不依赖请求体或 Content-Type，因此登出、生产停止、账号解锁等空 POST 也受保护。

- `Origin` 必须与请求的协议、主机及有效端口相同；缺失时检查 `Referer`。
  `null`、非法或多值来源不会被当成“缺失”放行。
- 携带 `Sec-Fetch-Site` 时只接受 `same-origin`，不信任 `same-site`。
- 拒绝返回 403 ProblemDetails，错误码 `csrf_origin_rejected`，带关联 ID，且不执行端点副作用。
- 显式 Bearer 认证不依赖环境 Cookie，保持原有行为；服务令牌仍优先于 Cookie。
  不携带浏览器来源头的现有非浏览器 Cookie 调用保持兼容；自动化客户端建议使用 Bearer。
- CORS 来源列表不作为 Cookie 写请求白名单。前端开发代理应保留 Host；
  TLS 终止代理应按 `ReverseProxy` 可信边界配置转发协议及 Host，不能直接信任任意转发头。

登录与登出共用 Cookie 属性：`Path=/`、`HttpOnly`、`SameSite=Strict`、`IsEssential`，
`Secure` 取可信代理处理后的 HTTPS 状态；登出只改变 Cookie 值和过期时间，并撤销服务端会话。
回归用例见 `CookieCsrfTests`，覆盖同主机跨端口、来源缺失/非法、Bearer、
HTTP/HTTPS Cookie 属性以及可信代理后的外部端口。

## 迁移注意

v28 用 `customApply` 而不是裸 `ALTER TABLE`：`security_users` 表的创建与版本迁移存在两条路径
（全新库 vs 旧库升级），裸加列在"表还不存在"或"列已存在"这两种情况下会直接失败。
`ApplyLoginLockoutColumnsAsync` 先查 `sqlite_master` 再两次 `EnsureColumnAsync`，两条路径都幂等。

同时这也意味着：**不要为了改列定义去动 v28 的 SQL**。迁移带 checksum 校验，历史一旦被改写，
所有已有数据库都会报 `Schema migration checksum drift detected`，而正确处置方式只能是
清掉本地开发库（生产库不能清）。
