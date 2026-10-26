# 自定义协议通信（device.tcpRequest / device.udpMessage / device.serialPort）

日期：2026-10-26 · 节点数 177 → 180

## 为什么加它们

现场大量设备只有私有协议：扫码枪、光源控制器、拧紧枪、力传感器。它们既没有 HTTP，也没有 PLC 寄存器，
`device.*Tag` 的 Modbus/S7 标签模型覆盖不到。此前只能靠上位机转接或写插件。

三个节点按传输层划分，**报文语义完全一致**（同一套参数名、终止符、读取模式、正则校验），
所以一条配方从 TCP 换成串口时只需要改端点参数。

| 节点 | 传输 | 典型设备 |
|---|---|---|
| `device.tcpRequest` | TCP 客户端，一次请求一条连接 | 网口扫码枪、光源控制器、以太网 ASCII 仪器 |
| `device.udpMessage` | UDP 数据报，可选等一条应答 | 广播式设备、简单以太网协议 |
| `device.serialPort` | RS-232/485，每次开关串口 | 手持扫码枪、串口光源、拧紧枪 |

## 共同报文语义

| 参数 | 说明 |
|---|---|
| `payloadEncoding` | `text`（UTF-8）或 `hex`（`02 31 0D`，空格/`-`/`:` 忽略）。多数字节协议都能直接写 |
| `sendTerminator` | `none`/`cr`/`lf`/`crlf` |
| `readMode` | `untilTerminator` / `fixedLength` / `none`（不读应答） |
| `readTerminator` / `readLength` | 读取界定；终止符会从应答里剥离后再做正则校验 |
| `maxResponseBytes` | 应答硬上限，超限**失败**而不是截断 |
| `expectPattern` + `requireMatch` | 可选正则校验：默认只置 `matched=false`，开启 requireMatch 才失败 |

输出端口三节点一致：`response`（UTF-8 文本）、`responseHex`、`bytesRead`、`matched`、`elapsedMs`、`diagnostics`。

## 安全：不是裸 socket

三个节点都按设备节点建模：

- 目录声明 `Actions: [DeviceWrite/Synchronous]` + `Resources: [("device","deviceId")]`，
  因此调试运行的动作审批、追溯、权限判断都会把它们当设备写入处理（与 `device.writeTag` 同级）。
- 真实派发前后持有 `IDeviceCommandOwnershipGate` 许可（`deviceId` 作为资源 ID）：
  **租约被释放或换代后，请求根本不会发出**。这不是"节点里自己加的保护"，而是与 PLC/机器人写入共用的同一道闸门。
- `Capabilities.Deterministic=false / SupportsParallel=false / SupportsRunNode=false`：不参与流水线融合，
  不会在并行分支里并发发命令。

## 超时与重发的态度

超时一律关闭连接/端口并失败，错误信息明确写出"请求可能已到达对端，重试前请核对设备状态"。
**节点自身绝不自动重试**：私有协议通常没有幂等键，自动重发可能造成重复动作（重复拧紧、重复喷胶）。
需要重试时由配方显式用 `flow.while` / `flow.try` 表达，并自行承担语义。

## 实现细节

| 关注点 | 处理 |
|---|---|
| 连接复用 | 不复用。一次请求一条连接，避免连接池、半包与会话泄漏；串口在请求之间不占用（否则一条卡住的配方会锁死工位） |
| 串口取消 | .NET 的 `SerialPort.BaseStream.ReadAsync` 在 Windows 上忽略 `CancellationToken`，因此改为同步 `Read` + `ReadTimeout` 逐字节读取，字节间检查取消 |
| 端口占用 | `UnauthorizedAccessException` 归类为"端口可能被其他程序占用" |
| 应答读取 | TCP 分块读、串口逐字节读；两者都按终止符/固定长度界定，并在短包时明确失败而不是返回半帧 |
| 正则预算 | 固定 100 ms 与 4096 字符上限，避免上游文本触发灾难性回溯 |
| DTR/RTS | 串口可配置，部分 RS-485 转换器与老设备依赖电平 |

## 配置示例

网口扫码枪（ASCII，CRLF 结尾，返回 "OK" 才算成功）：

```json
{"type":"device.tcpRequest","parameters":{
  "deviceId":"scanner-1","host":"192.168.1.50","port":9004,
  "payload":"LON","sendTerminator":"crlf",
  "readMode":"untilTerminator","readTerminator":"crlf",
  "expectPattern":"^OK","requireMatch":true,"timeoutMs":800}}
```

光源控制器（二进制帧，固定 3 字节应答）：

```json
{"type":"device.serialPort","parameters":{
  "deviceId":"light-1","portName":"COM3","baudRate":9600,
  "payload":"02 41 03","payloadEncoding":"hex",
  "readMode":"fixedLength","readLength":3}}
```

## 验证

`CustomProtocolNodeTests` 10 项：

| 用例 | 覆盖点 |
|---|---|
| `Tcp_SendsTerminatedRequestAndStripsReplyTerminator` | 真实回环收发、CRLF 终止符、应答剥离、正则匹配 |
| `Tcp_SupportsHexPayloadAndFixedLengthReply` | 十六进制报文、固定长度读取 |
| `Tcp_FailsWithActionableMessageWhenPeerDoesNotReply` | 超时错误信息含毫秒数且提示"可能已到达对端" |
| `Tcp_RejectsOversizedReply` | 应答超限失败 |
| `Tcp_RequireMatchTurnsMismatchIntoFailure` | 默认只置 matched=false；requireMatch 才失败 |
| `Tcp_RejectsInvalidConfigurationBeforeTouchingNetwork` | 端口越界、固定长度 0、奇数十六进制串 |
| `Udp_SendsDatagramAndReadsReply` | 真实回环数据报收发 |
| `Udp_CanSendWithoutWaitingForReply` | 不等待应答模式 |
| `Serial_ValidatesConfigurationWithoutHardware` | 串口名空、校验位非法、固定长度 0（CI 无硬件，不做真实 I/O） |
| `Catalog_DeclaresProtocolNodesAsDeviceActions` | 三个节点都声明 DeviceWrite 动作与 device 资源 |

## 已知限制

- **不做长连接/订阅**。需要持续推送的设备（如主动上报的传感器）应走 `device.readTag` 的轮询模型或单独驱动。
- **串口没有硬件级集成测试**。CI 无串口，只有配置校验覆盖；真实波特率/流控行为需现场验证。
- **不做 TCP 服务器模式**（被动接受连接），也不支持 TLS。
- 报文不做自动转义/校验和计算；需要校验和时用 `value.string` 组包后以 hex 方式发送。
