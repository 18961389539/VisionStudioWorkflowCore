using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using VisionStudio.Engine.Device;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 私有协议报文收发的公共部分：编码、终止符、按模式读取应答、正则校验。
/// 三个传输节点（TCP / UDP / 串口）共用同一套语义，避免"同一个终止符在三个节点里三种解释"。
/// </summary>
internal static class ProtocolFraming
{
    public const int MaxRequestBytes = 65536;
    public const int MaxResponseCeiling = 1048576;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>按 text（UTF-8）或 hex（"0D 0A" / "0D0A" 均可）把参数转换成发送字节。</summary>
    public static byte[] EncodePayload(string payload, string encoding)
    {
        if (encoding.Equals("hex", StringComparison.OrdinalIgnoreCase))
        {
            var compact = new string(payload.Where(ch => !char.IsWhiteSpace(ch) && ch is not '-' and not ':').ToArray());
            if (compact.Length == 0) return [];
            if (compact.Length % 2 != 0)
                throw new InvalidOperationException("protocol payload hex encoding requires an even number of digits.");
            var bytes = new byte[compact.Length / 2];
            for (var index = 0; index < bytes.Length; index++)
            {
                if (!byte.TryParse(compact.AsSpan(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[index]))
                    throw new InvalidOperationException($"protocol payload hex encoding contains a non-hex pair at position {index * 2}: '{compact.Substring(index * 2, 2)}'.");
            }
            return bytes;
        }
        return Encoding.UTF8.GetBytes(payload);
    }

    /// <summary>终止符：none / cr / lf / crlf。</summary>
    public static byte[] Terminator(string name) => name.Trim().ToLowerInvariant() switch
    {
        "none" or "" => [],
        "cr" => [0x0D],
        "lf" => [0x0A],
        "crlf" => [0x0D, 0x0A],
        _ => throw new InvalidOperationException($"terminator '{name}' is not supported; use none, cr, lf or crlf.")
    };

    public static string ToHex(ReadOnlySpan<byte> bytes)
        => bytes.Length == 0 ? string.Empty : Convert.ToHexString(bytes).Chunk(2).Select(chunk => new string(chunk)).Aggregate((left, right) => left + " " + right);

    public static int RequireRange(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
            throw new InvalidOperationException($"{name} must be within {minimum}..{maximum}; got {value}.");
        return value;
    }

    /// <summary>读取应答：直到终止符、固定长度或空。</summary>
    public static async Task<byte[]> ReadResponseAsync(
        Stream stream,
        string readMode,
        byte[] readTerminator,
        int readLength,
        int maxResponseBytes,
        CancellationToken cancellationToken)
    {
        if (readMode.Equals("none", StringComparison.OrdinalIgnoreCase)) return [];

        var buffer = new byte[1024];
        using var collected = new MemoryStream();
        while (true)
        {
            if (readMode.Equals("fixedLength", StringComparison.OrdinalIgnoreCase) && collected.Length >= readLength) break;

            var want = readMode.Equals("fixedLength", StringComparison.OrdinalIgnoreCase)
                ? Math.Min(buffer.Length, readLength - (int)collected.Length)
                : buffer.Length;
            var read = await stream.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            collected.Write(buffer, 0, read);

            if (collected.Length > maxResponseBytes)
                throw new InvalidOperationException($"protocol response exceeded maxResponseBytes={maxResponseBytes}; increase the limit deliberately instead of silently truncating.");

            if (readMode.Equals("untilTerminator", StringComparison.OrdinalIgnoreCase) && readTerminator.Length > 0)
            {
                var bytes = collected.GetBuffer().AsSpan(0, (int)collected.Length);
                if (bytes.EndsWith(readTerminator)) break;
            }
        }

        var result = collected.ToArray();
        if (readMode.Equals("untilTerminator", StringComparison.OrdinalIgnoreCase) && readTerminator.Length > 0)
        {
            if (!result.AsSpan().EndsWith(readTerminator))
                throw new InvalidOperationException($"protocol response did not end with the configured terminator ({ToHex(readTerminator)}) within the timeout; got {result.Length} bytes.");
            result = result[..^readTerminator.Length];
        }
        if (readMode.Equals("fixedLength", StringComparison.OrdinalIgnoreCase) && result.Length < readLength)
            throw new InvalidOperationException($"protocol response returned {result.Length} bytes but readLength={readLength} was requested.");
        return result;
    }

    /// <summary>可选正则校验；返回是否匹配。pattern 为空视为不校验（matched=true）。</summary>
    public static bool Matches(string text, string pattern, bool ignoreCase, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return true;
        if (pattern.Length > 4096) throw new InvalidOperationException("expectPattern must be at most 4096 characters.");
        var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        try
        {
            return Regex.IsMatch(text, pattern, options, MatchTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            throw new InvalidOperationException($"expectPattern evaluation exceeded {MatchTimeout.TotalMilliseconds} ms.");
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("expectPattern is not a valid regular expression: " + ex.Message, ex);
        }
    }

    /// <summary>统一构造结果；三个节点共用一套端口，避免下游按传输类型换接线。</summary>
    public static NodeExecutorResult Result(
        string type,
        string deviceId,
        string endpoint,
        string transport,
        byte[] response,
        bool matched,
        double elapsedMs,
        Dictionary<string, object?> detail)
    {
        var text = Encoding.UTF8.GetString(response);
        var diagnostics = CoordinateValidation.Diagnostics(
            type,
            [("deviceId", deviceId), ("endpoint", endpoint), ("transport", transport),
             ("bytesRead", response.Length), ("matched", matched), ("elapsedMs", elapsedMs),
             ("responseHex", ToHex(response.AsSpan(0, Math.Min(response.Length, 256))))]);
        var summary = new Dictionary<string, object?>(detail)
        {
            ["deviceId"] = deviceId,
            ["endpoint"] = endpoint,
            ["transport"] = transport,
            ["bytesRead"] = response.Length,
            ["matched"] = matched,
            ["elapsedMs"] = elapsedMs,
            ["response"] = text.Length <= 200 ? text : text[..200] + "…",
            ["diagnostics"] = diagnostics
        };
        return new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["response"] = VisionValue.String(text),
                ["responseHex"] = VisionValue.String(ToHex(response)),
                ["bytesRead"] = VisionValue.Integer(response.Length),
                ["matched"] = VisionValue.Boolean(matched),
                ["elapsedMs"] = VisionValue.Double(elapsedMs),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            summary);
    }
}

/// <summary>
/// 自定义 TCP 协议请求（device.tcpRequest，2026-10-26 · 缺口分析 P0-7）。
///
/// 定位：现场大量设备只有私有 ASCII/二进制协议（扫码枪、光源控制器、拧紧枪、力传感器），既没有 HTTP 也没有
/// PLC 寄存器。此前只能靠上位机转接或写插件。
///
/// 传输语义刻意保持"一次请求一条连接"：
/// - 不维持长连接，避免引入连接池状态、半包粘包与会话泄漏；
/// - 每次请求独立超时，超时即关闭连接，不重发（协议是否已到达对端未知，自动重发可能造成重复动作）；
/// - 真实派发前后持有设备所有权许可（IDeviceCommandOwnershipGate），与 PLC/机器人写入共用同一套安全闸门。
/// </summary>
public sealed class TcpRequestNode(IDeviceCommandOwnershipGate? ownershipGate = null) : IVisionNodeExecutor
{
    public string Type => "device.tcpRequest";

    public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var deviceId = DeviceNodeValidation.RequireId(node.GetString("deviceId", "tcp-1"), "device.tcpRequest deviceId");
        var host = node.GetString("host", "127.0.0.1").Trim();
        if (host.Length == 0) throw new InvalidOperationException("device.tcpRequest host must be non-empty.");
        var port = ProtocolFraming.RequireRange(node.GetInt("port", 5000), 1, 65535, "device.tcpRequest port");
        var timeoutMs = ProtocolFraming.RequireRange(node.GetInt("timeoutMs", 1000), 50, 60000, "device.tcpRequest timeoutMs");
        var maxResponse = ProtocolFraming.RequireRange(node.GetInt("maxResponseBytes", 4096), 16, ProtocolFraming.MaxResponseCeiling, "device.tcpRequest maxResponseBytes");
        var readMode = ReadMode(node);
        var readTerminator = ProtocolFraming.Terminator(node.GetString("readTerminator", "crlf"));
        var readLength = ProtocolFraming.RequireRange(node.GetInt("readLength", 0), 0, 65535, "device.tcpRequest readLength");
        if (readMode == "fixedLength" && readLength == 0)
            throw new InvalidOperationException("device.tcpRequest readLength must be > 0 when readMode=fixedLength.");

        var payload = context.TryGetValue("payload", out var connected)
            ? connected.Require<string>("payload")
            : node.GetString("payload", string.Empty);
        var request = ProtocolFraming.EncodePayload(payload, node.GetString("payloadEncoding", "text"));
        var terminator = ProtocolFraming.Terminator(node.GetString("sendTerminator", "none"));
        var frame = request.Concat(terminator).ToArray();
        if (frame.Length > ProtocolFraming.MaxRequestBytes)
            throw new InvalidOperationException($"device.tcpRequest request is {frame.Length} bytes; the limit is {ProtocolFraming.MaxRequestBytes}.");
        if (readMode != "none" && frame.Length == 0)
            throw new InvalidOperationException("device.tcpRequest requires a non-empty payload when a response is expected.");

        var endpoint = $"{host}:{port}";
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
        byte[] response;
        // 派发边界：所有权失效（租约被释放/换代）时闸门抛错，请求根本不会发出。
        using var permit = (ownershipGate ?? AllowAllDeviceCommandOwnershipGate.Instance)
            .AcquireCommandPermit(DeviceCommandResourceKinds.Device, deviceId);
        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            if (frame.Length > 0) await stream.WriteAsync(frame, timeout.Token).ConfigureAwait(false);
            await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
            response = await ProtocolFraming.ReadResponseAsync(stream, readMode, readTerminator, readLength, maxResponse, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"device.tcpRequest '{deviceId}' timed out after {timeoutMs} ms talking to {endpoint}. " +
                "The request may already have reached the peer; verify device state before retrying.");
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"device.tcpRequest '{deviceId}' failed talking to {endpoint}: {ex.SocketErrorCode} {ex.Message}", ex);
        }
        stopwatch.Stop();

        var text = Encoding.UTF8.GetString(response);
        var matched = ProtocolFraming.Matches(text, node.GetString("expectPattern", string.Empty), node.GetBool("ignoreCase", false), cancellationToken);
        if (!matched && node.GetBool("requireMatch", false))
            throw new InvalidOperationException(
                $"device.tcpRequest '{deviceId}' response from {endpoint} did not match expectPattern '{node.GetString("expectPattern", string.Empty)}'; got '{Clip(text)}'.");

        return ProtocolFraming.Result(Type, deviceId, endpoint, "tcp", response, matched, stopwatch.Elapsed.TotalMilliseconds,
            new Dictionary<string, object?> { ["requestBytes"] = frame.Length, ["readMode"] = readMode });
    }

    internal static string ReadMode(NodeDefinition node)
    {
        var mode = node.GetString("readMode", "untilTerminator").Trim().ToLowerInvariant();
        return mode switch
        {
            "none" => "none",
            "untilterminator" => "untilTerminator",
            "fixedlength" => "fixedLength",
            _ => throw new InvalidOperationException($"readMode '{mode}' is not supported; use none, untilTerminator or fixedLength.")
        };
    }

    internal static string Clip(string text) => text.Length <= 120 ? text : text[..120] + "…";
}

/// <summary>
/// 自定义 UDP 协议（device.udpMessage）。与 TCP 共用报文语义，但按数据报收发：
/// 每次请求一个数据报，可等待一个应答数据报。
/// </summary>
public sealed class UdpMessageNode(IDeviceCommandOwnershipGate? ownershipGate = null) : IVisionNodeExecutor
{
    public string Type => "device.udpMessage";

    public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var deviceId = DeviceNodeValidation.RequireId(node.GetString("deviceId", "udp-1"), "device.udpMessage deviceId");
        var host = node.GetString("host", "127.0.0.1").Trim();
        if (host.Length == 0) throw new InvalidOperationException("device.udpMessage host must be non-empty.");
        var port = ProtocolFraming.RequireRange(node.GetInt("port", 5001), 1, 65535, "device.udpMessage port");
        var timeoutMs = ProtocolFraming.RequireRange(node.GetInt("timeoutMs", 1000), 50, 60000, "device.udpMessage timeoutMs");
        var payload = context.TryGetValue("payload", out var connected)
            ? connected.Require<string>("payload")
            : node.GetString("payload", string.Empty);
        var frame = ProtocolFraming.EncodePayload(payload, node.GetString("payloadEncoding", "text"));
        var terminator = ProtocolFraming.Terminator(node.GetString("sendTerminator", "none"));
        var datagram = frame.Concat(terminator).ToArray();
        if (datagram.Length > ProtocolFraming.MaxRequestBytes)
            throw new InvalidOperationException($"device.udpMessage payload is {datagram.Length} bytes; the limit is {ProtocolFraming.MaxRequestBytes}.");

        var expectsReply = node.GetBool("expectReply", true);
        var endpoint = $"{host}:{port}";
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
        byte[] response = [];
        using var permit = (ownershipGate ?? AllowAllDeviceCommandOwnershipGate.Instance)
            .AcquireCommandPermit(DeviceCommandResourceKinds.Device, deviceId);
        try
        {
            using var client = new UdpClient();
            await client.SendAsync(datagram, new System.Net.IPEndPoint(System.Net.IPAddress.Parse(await ResolveAsync(host, timeout.Token)), port), timeout.Token).ConfigureAwait(false);
            if (expectsReply)
            {
                var received = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                response = received.Buffer;
                if (response.Length > ProtocolFraming.MaxResponseCeiling)
                    throw new InvalidOperationException($"device.udpMessage reply exceeded {ProtocolFraming.MaxResponseCeiling} bytes.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"device.udpMessage '{deviceId}' timed out after {timeoutMs} ms waiting for a reply from {endpoint}.");
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"device.udpMessage '{deviceId}' failed talking to {endpoint}: {ex.SocketErrorCode} {ex.Message}", ex);
        }
        stopwatch.Stop();

        var text = Encoding.UTF8.GetString(response);
        var matched = ProtocolFraming.Matches(text, node.GetString("expectPattern", string.Empty), node.GetBool("ignoreCase", false), cancellationToken);
        if (!matched && node.GetBool("requireMatch", false))
            throw new InvalidOperationException($"device.udpMessage '{deviceId}' reply from {endpoint} did not match expectPattern; got '{TcpRequestNode.Clip(text)}'.");

        return ProtocolFraming.Result(Type, deviceId, endpoint, "udp", response, matched, stopwatch.Elapsed.TotalMilliseconds,
            new Dictionary<string, object?> { ["sentBytes"] = datagram.Length, ["expectReply"] = expectsReply });
    }

    private static async Task<string> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (System.Net.IPAddress.TryParse(host, out var literal)) return literal.ToString();
        var addresses = await System.Net.Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        return addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)?.ToString()
            ?? throw new InvalidOperationException($"device.udpMessage cannot resolve '{host}' to an IPv4 address.");
    }
}

/// <summary>
/// 自定义串口协议（device.serialPort）。面向光源控制器、扫码枪、拧紧枪这类只有 RS-232/485 文本协议的设备。
///
/// 语义与 TCP 节点一致（同参数命名、同应答模式、同正则校验），差别只在传输层：
/// - 每次请求打开-发送-接收-关闭；串口是独占资源，长期占用会挡住其他工位与手动调试；
/// - DTR/RTS 可配（不少 485 转换器与老设备依赖其电平）；
/// - 超时后关闭端口，不重发。
/// </summary>
public sealed class SerialRequestNode(IDeviceCommandOwnershipGate? ownershipGate = null) : IVisionNodeExecutor
{
    public string Type => "device.serialPort";

    public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var deviceId = DeviceNodeValidation.RequireId(node.GetString("deviceId", "serial-1"), "device.serialPort deviceId");
        var portName = node.GetString("portName", "COM1").Trim();
        if (portName.Length == 0) throw new InvalidOperationException("device.serialPort portName must be non-empty.");
        var baudRate = ProtocolFraming.RequireRange(node.GetInt("baudRate", 9600), 50, 4_000_000, "device.serialPort baudRate");
        var dataBits = ProtocolFraming.RequireRange(node.GetInt("dataBits", 8), 5, 8, "device.serialPort dataBits");
        var timeoutMs = ProtocolFraming.RequireRange(node.GetInt("timeoutMs", 1000), 50, 60000, "device.serialPort timeoutMs");
        var maxResponse = ProtocolFraming.RequireRange(node.GetInt("maxResponseBytes", 4096), 16, ProtocolFraming.MaxResponseCeiling, "device.serialPort maxResponseBytes");
        var parity = node.GetString("parity", "none").Trim().ToLowerInvariant() switch
        {
            "none" => Parity.None,
            "odd" => Parity.Odd,
            "even" => Parity.Even,
            "mark" => Parity.Mark,
            "space" => Parity.Space,
            _ => throw new InvalidOperationException("device.serialPort parity must be none, odd, even, mark or space.")
        };
        var stopBits = node.GetString("stopBits", "one").Trim().ToLowerInvariant() switch
        {
            "one" => StopBits.One,
            "onepointfive" => StopBits.OnePointFive,
            "two" => StopBits.Two,
            _ => throw new InvalidOperationException("device.serialPort stopBits must be one, onePointFive or two.")
        };
        var readMode = TcpRequestNode.ReadMode(node);
        var readTerminator = ProtocolFraming.Terminator(node.GetString("readTerminator", "crlf"));
        var readLength = ProtocolFraming.RequireRange(node.GetInt("readLength", 0), 0, 65535, "device.serialPort readLength");
        if (readMode == "fixedLength" && readLength == 0)
            throw new InvalidOperationException("device.serialPort readLength must be > 0 when readMode=fixedLength.");

        var payload = context.TryGetValue("payload", out var connected)
            ? connected.Require<string>("payload")
            : node.GetString("payload", string.Empty);
        var frame = ProtocolFraming.EncodePayload(payload, node.GetString("payloadEncoding", "text"))
            .Concat(ProtocolFraming.Terminator(node.GetString("sendTerminator", "crlf"))).ToArray();
        if (frame.Length == 0) throw new InvalidOperationException("device.serialPort requires a non-empty payload.");
        if (frame.Length > ProtocolFraming.MaxRequestBytes)
            throw new InvalidOperationException($"device.serialPort request is {frame.Length} bytes; the limit is {ProtocolFraming.MaxRequestBytes}.");

        var endpoint = $"{portName}@{baudRate},{dataBits},{parity},{stopBits}";
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
        byte[] response;
        using var permit = (ownershipGate ?? AllowAllDeviceCommandOwnershipGate.Instance)
            .AcquireCommandPermit(DeviceCommandResourceKinds.Device, deviceId);
        try
        {
            using var port = new SerialPort(portName, baudRate, parity, dataBits, stopBits)
            {
                Handshake = Handshake.None,
                DtrEnable = node.GetBool("dtrEnable", false),
                RtsEnable = node.GetBool("rtsEnable", false),
                ReadTimeout = timeoutMs,
                WriteTimeout = timeoutMs
            };
            port.Open();
            port.Write(frame, 0, frame.Length);
            // 串口读不支持可靠的异步取消（Windows 上 BaseStream.ReadAsync 无视 CancellationToken），
            // 因此用同步 Read + ReadTimeout 逐字节读，并在每字节之间检查取消，避免超时后仍挂住。
            response = await Task.Run(() => ReadSerial(port, readMode, readTerminator, readLength, maxResponse, timeout.Token), timeout.Token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException($"device.serialPort '{deviceId}' timed out after {timeoutMs} ms on {portName}. The command may already have reached the device; verify state before retrying.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"device.serialPort '{deviceId}' timed out after {timeoutMs} ms on {portName}. Verify device state before retrying.");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"device.serialPort '{deviceId}' cannot open {portName}: {ex.Message}. Another program may hold the port.", ex);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"device.serialPort '{deviceId}' failed on {portName}: {ex.Message}", ex);
        }
        stopwatch.Stop();

        var text = Encoding.UTF8.GetString(response);
        var matched = ProtocolFraming.Matches(text, node.GetString("expectPattern", string.Empty), node.GetBool("ignoreCase", false), cancellationToken);
        if (!matched && node.GetBool("requireMatch", false))
            throw new InvalidOperationException($"device.serialPort '{deviceId}' response from {portName} did not match expectPattern; got '{TcpRequestNode.Clip(text)}'.");

        return ProtocolFraming.Result(Type, deviceId, endpoint, "serial", response, matched, stopwatch.Elapsed.TotalMilliseconds,
            new Dictionary<string, object?> { ["requestBytes"] = frame.Length, ["readMode"] = readMode });
    }

    /// <summary>同步读取串口应答：ReadTimeout 由端口配置承担，逐字节读取以便每字节检查取消。</summary>
    private static byte[] ReadSerial(SerialPort port, string readMode, byte[] readTerminator, int readLength, int maxResponseBytes, CancellationToken cancellationToken)
    {
        if (readMode.Equals("none", StringComparison.OrdinalIgnoreCase)) return [];
        using var collected = new MemoryStream();
        var single = new byte[1];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (readMode.Equals("fixedLength", StringComparison.OrdinalIgnoreCase) && collected.Length >= readLength) break;
            int read;
            try
            {
                read = port.Read(single, 0, 1);
            }
            catch (TimeoutException)
            {
                // 固定长度模式读到部分数据、或直到终止符模式没等到终止符：都要明确报错，不能静默返回半包。
                if (readMode.Equals("untilTerminator", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"protocol response did not end with the configured terminator within the timeout; got {collected.Length} bytes.");
                if (readMode.Equals("fixedLength", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"protocol response returned {collected.Length} bytes but readLength={readLength} was requested.");
                throw;
            }
            if (read <= 0) break;
            collected.WriteByte(single[0]);
            if (collected.Length > maxResponseBytes)
                throw new InvalidOperationException($"protocol response exceeded maxResponseBytes={maxResponseBytes}; increase the limit deliberately instead of silently truncating.");
            if (readMode.Equals("untilTerminator", StringComparison.OrdinalIgnoreCase) && readTerminator.Length > 0)
            {
                var bytes = collected.GetBuffer().AsSpan(0, (int)collected.Length);
                if (bytes.EndsWith(readTerminator)) break;
            }
        }
        var result = collected.ToArray();
        if (readMode.Equals("untilTerminator", StringComparison.OrdinalIgnoreCase) && readTerminator.Length > 0)
        {
            if (!result.AsSpan().EndsWith(readTerminator))
                throw new InvalidOperationException($"protocol response did not end with the configured terminator ({ProtocolFraming.ToHex(readTerminator)}); got {result.Length} bytes.");
            result = result[..^readTerminator.Length];
        }
        if (readMode.Equals("fixedLength", StringComparison.OrdinalIgnoreCase) && result.Length < readLength)
            throw new InvalidOperationException($"protocol response returned {result.Length} bytes but readLength={readLength} was requested.");
        return result;
    }
}
