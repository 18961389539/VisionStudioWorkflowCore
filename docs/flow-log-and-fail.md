# 流程内日志与主动失败（flow.log / flow.fail）

日期：2026-10-26 · 节点数 175 → 177

## 为什么加它们

在此之前，流程内部**说不出话**：

- 排障只能看每个节点的 `diagnostics` 与运行报告，无法表达"这一轮用了哪个配方""为什么走了兜底分支"；
- 想主动终止只能在末尾接一个"必定失败"的节点（比如除零、接不上必需输入），错误信息还是那个节点报的，
  与实际原因无关。

两个节点分别补这两件事，并且刻意分工：

| 节点 | 作用 | 是否中断运行 |
|---|---|---|
| `flow.log` | 把自定义文本按级别记到宿主日志与运行报告 | 否 |
| `flow.fail` | 用明确原因终止整次运行 | 是 |
| `flow.result`（既有） | 判 OK / NG 处置 | 否 |

## `flow.log`

| 端口/参数 | 说明 |
|---|---|
| `exec` / `next` | 控制流 |
| `message`（可选输入） | 接线时覆盖同名参数 |
| `level`（参数，select） | `debug` / `info` / `warning` / `error` |
| 输出 `message` / `level` / `severity` / `logged` / `diagnostics` | `severity` 是 0..3，方便直接接 PLC 报警字或数值判断 |

两个落点：

1. **宿主日志**：通过 `Microsoft.Extensions.Logging` 写入，带 `[NodeId]`，级别按参数映射（debug→Debug、info→Information、
   warning→Warning、error→Error）。
2. **运行报告**：节点摘要里带 `level` / `severity` / `message`，追溯面板展开该节点即可看到。

设计取舍：

- **不做模板插值。** 拼字符串用 `value.string`（已支持模板与数值格式化），本节点只负责"按级别记下来"，
  避免出现第二套模板语法。
- **空文本不失败。** 日志是观测手段，不该把产线跑停。空消息照记，便于确认"分支确实走到了"。
- **超长失败而不是截断。** 上限 4096 字符；超过直接报错，避免把整幅 OCR 文本或大 JSON 静默塞进日志。
- **级别拼错直接失败**，不会默默按 info 处理。

## `flow.fail`

| 端口/参数 | 说明 |
|---|---|
| `exec` / `next` | `next` 仅为让图结构可连接而保留，**其后的节点不会执行** |
| `message`（可选输入） | 接线时覆盖同名参数；空白回退到默认句 |
| `code`（参数） | 短代码，1-64 字符，拼成 `[CODE] 原因` |

运行结果：`Success=false`，`Error` 形如 `failNodeId: [E_STOP] 主动终止`；追溯里该节点标记为失败并保留同一条消息。

实现选择的是**抛异常**而不是置标志位：运行器已经把节点异常统一收敛成"失败报告 + 运行结果 Error"，
抛异常即可拿到正确的用户可见消息与追溯，不需要给 `WorkflowRunResult` 新增字段，也不改变既有失败路径。

### 已知边界

`flow.try` 的执行体只接受可流水线化的确定性内置节点（`ValidateTryBody` 要求 `ExecutionMode=Pipeline`、
`Actions` 为空），因此 **`flow.fail` 不能放进 try 体里去走错误分支**。需要"可恢复的错误"时：

- 在 try 体里用会失败的测量/判定节点（例如搜索不到边缘）；或
- 直接用 `flow.result` 表达 NG 并继续后续留证/上传。

## 配置示例

```json
{"type":"flow.log","parameters":{"level":"warning","message":"未找到边缘，回退到固定 ROI"}}
{"type":"flow.fail","parameters":{"code":"E_ROBOT_TIMEOUT","message":"机器人 3 秒内未回位"}}
```

带测量的日志（先用 `value.string` 组包，再接到 `flow.log.message`）：

```
measure.caliperWidth.width ─┐
                            ├─ value.string(template: "宽度={value} 判定={a}") ─ flow.log.message
flow.result.disposition    ─┘
```

## 验证

`FlowObservabilityTests` 7 项：

| 用例 | 断言 |
|---|---|
| `Log_EmitsMessageLevelAndSeverity` | message/level/severity 正确回显 |
| `Log_InputOverridesParameterAndEmptyTextIsAllowed` | 端口覆盖参数；空文本照记不失败 |
| `Log_RejectsUnknownLevelAndOversizedText` | 未知级别、超 4096 字符均失败 |
| `Fail_ThrowsWithCodeAndMessage` | 异常消息严格等于 `[CODE] 原因` |
| `Fail_InputOverridesParameterAndBlankFallsBack` | 端口覆盖；空白回退默认句；空 code 失败 |
| `Fail_StopsRunAndSurfacesReason` | 运行失败、`Error` 含代码与原因、后续节点不执行、失败节点报告带原因 |
| `Log_RunsInlineAndExposesLevelInReport` | 运行成功、后续节点仍执行、报告摘要含级别与消息 |

Engine 全量测试随后重跑；前端 i18n（节点名/描述/参数/选项/端口/分组）与图标（`log` / `stop` 两个新图形）已补齐。
