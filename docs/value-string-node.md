# 字符串加工节点（value.string）

日期：2026-10-26 · 节点数 171 → 172

## 为什么加它

目录里产出 `String` 的节点很多（`recognition.barcode` 的 `text`、`recognition.ocr` 的文本、`image.acquire` 的 `timestamp`、`output.saveImage` 的 `path`、各节点的 `diagnostics`），
但此前**没有任何节点能加工字符串**：`text.match` / `text.validate` / `text.regex` 只做校验与匹配，`output.httpRequest` 与 `device.writeTag` 只能原样接收。
结果是"条码文本 + 判定 + 坐标"这类最基本的上报串在画布上拼不出来，只能绕到外部服务。

`value.string` 补的就是这一段：11 种运算，一个节点覆盖拼接、格式转换、截取、清洗与补齐。

## 接口

| 端口 | 方向 | 类型 | 说明 |
|---|---|---|---|
| `exec` | 入 | Control | — |
| `a` `b` `c` `d` | 入 | String（可选） | 未接线按空串处理；`a` 在单值运算里为必接（编译期按 `op` 校验） |
| `value` | 入 | Double（可选） | 仅"数值转文本"必接；模板中的 `{value}` 也读这里 |
| `textValue` | 出 | String | 加工结果 |
| `charCount` | 出 | Integer | 结果的 UTF-16 码元长度 |
| `numberValue` | 出 | Double（可选） | **仅"文本转数值"输出**，其它运算不输出（0 会被误读成"转出来是 0"） |
| `truncated` | 出 | Boolean | 子串是否被裁剪 |
| `diagnostics` | 出 | String | JSON 诊断（`op`、`charCount`、`truncated`、结果前 200 字符、运算细节） |

## 运算语义

| op | 语义 | 失败条件 |
|---|---|---|
| `template` | `{a}{b}{c}{d}{value}` 与序号别名 `{0}..{3}`；`{{` `}}` 输出字面花括号 | 占位符未知、花括号未闭合、引用 `{value}` 却没接输入 |
| `join` | 用 `separator` 连接 a..d 的**非空**段 | — |
| `numbertostring` | `value` 按 `format`（invariant 区域性）格式化，如 `0.###`、`F3`、`D6` | 格式串非法、`value` 非有限 |
| `tonumber` | `a` 去空白后按 `NumberStyles.Float` + invariant 解析 | 空串、非数字、非有限值 |
| `substring` | `startIndex` + `takeLength`（0=到结尾） | 下标为负；越界**不报错**，裁剪并置 `truncated=true` |
| `trim` | 按 `trimSide`（both/start/end）去 Unicode 空白 | 方向非法 |
| `replace` | 字面量或正则替换；`replaceAll=false` 只换第一处 | `find` 为空、正则非法、超过 100 ms 匹配预算 |
| `pad` | 补到 `padWidth`，`padChar` 单字符，`padSide` 左/右 | 补齐字符不是 1 个字符、宽度越界；已达宽度**不截断** |
| `upper` / `lower` | invariant 大小写转换 | — |
| `length` | 文本透传，长度看 `charCount` | — |

补充约定：

- 结果长度上限 `maxLength`（默认 65536，可配 16..1048576）。超出**报错**，不静默截断后存储。
- 正则替换的 `replaceWith` 一律按字面量写入，**不做 `$1` 捕获组展开**（用 `MatchEvaluator` 实现），避免用户输入 `$1` 时被静默改写。
- 长度与下标都以 UTF-16 码元计，与 `string.Length` 一致；不声称是字素簇或字节数。

## 典型用法

```
recognition.barcode.text ─┐
flow.result.disposition  ─┼─ value.string(template: "{a}-{b}-{value}") ─ device.writeTag / output.httpRequest
measure.caliperWidth     ─┘        (value 由数值输入接入)
```

## 验证

- 后端：`backend/tests/VisionStudio.Engine.Tests/StringNodeTests.cs` 10 项（模板/别名/转义、未知占位符、`{value}` 未接线、拼接跳空、数值格式、严格解析、子串裁剪、替换三种模式、补齐、非法配置、maxLength）。
- Engine 全量：**1844 项通过**（含 14 项目录契约测试）。
- 目录：`catalog.generated.json` 重新生成，`--check` 通过，172 节点 / hash `c56159c2…`。
- 前端：`catalogLocalization`（选项中文覆盖率）、`nodeIcons`、`toolbox` 通过；`tsc -b` 无错误。
- 已知与本轮无关的既有问题：`scripts/static-audit.py` 的"DI 注册"规则仍按 `AddSingleton<XxxNode>()` 逐类匹配，而宿主已改为反射注册（`AddBuiltInNodeExecutors`），因此对**全部 172 个节点**都报缺失，质量门禁在该规则上原本就是红的。
