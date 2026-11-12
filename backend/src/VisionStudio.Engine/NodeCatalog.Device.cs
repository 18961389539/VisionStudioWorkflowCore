namespace VisionStudio.Engine;

public static partial class BuiltInNodeCatalog
{
    private static NodeCatalogItem[] DeviceItems =>
    [
        new(
            "device.readTag",
            "Read Device Tag",
            "Device",
            [ExecIn],
            [
                new("value", VisionDataType.DeviceTagValue),
                new("boolValue", VisionDataType.Boolean),
                new("boolValueValid", VisionDataType.Boolean),
                new("numberValue", VisionDataType.Double),
                new("numberValueValid", VisionDataType.Boolean),
                new("textValue", VisionDataType.String),
                new("quality", VisionDataType.String),
                new("timestamp", VisionDataType.String),
                new("ageMs", VisionDataType.Double),
                new("isGoodQuality", VisionDataType.Boolean),
                new("diagnostics", VisionDataType.String), new("warningCount", VisionDataType.Integer), new("attemptCount", VisionDataType.Integer),
                Next
            ],
            [
                new("deviceId", "Device ID", "text", "virtual-modbus-1", Group: "Device", Description: "Non-empty device id registered in DeviceManager (max 128 chars)."),
                new("tagId", "Tag ID", "text", "trigger", Group: "Tag", Description: "Non-empty tag id on that device (max 128 chars)."),
                new("fresh", "Fresh Read", "boolean", false, Group: "Read", Description: "When true, force a device poll instead of returning a cached sample."),
                new("autoConnect", "Auto Connect", "boolean", true, Group: "Device", Description: "Connect the device automatically if it is currently disconnected."),
                new("requireGoodQuality", "Require Good Quality", "boolean", false, Group: "Read", Description: "Fail the node when the sample quality is not Good."),
                new("maxAgeMs", "Max Sample Age (ms, 0=off)", "number", 0, 0, 86400000, 100, Group: "Read", Description: "Reject samples older than this age in milliseconds; 0 disables the freshness gate."),
                new("rejectNullValue", "Reject Null Value", "boolean", false, Group: "Read", Description: "Fail when the tag sample value is null after a successful read.")
            ,









                ],
            "Read a typed device tag, quality and timestamp. boolValueValid/numberValueValid distinguish a converted false/zero from conversion failure. Numeric values must be finite; use the typed value or textValue to preserve Int64 precision.",
            "builtin",
            new VisionToolCapabilities(Deterministic: false, SupportsParallel: false),
            Resources: [new("device", "deviceId")]),
        new(
            "device.writeTag",
            "Write Device Tag",
            "Device",
            [ExecIn, new("value", VisionDataType.Any, Required: false)],
            [
                new("value", VisionDataType.DeviceTagValue),
                new("boolValue", VisionDataType.Boolean),
                new("boolValueValid", VisionDataType.Boolean),
                new("numberValue", VisionDataType.Double),
                new("numberValueValid", VisionDataType.Boolean),
                new("textValue", VisionDataType.String),
                new("quality", VisionDataType.String),
                new("timestamp", VisionDataType.String),
                new("ageMs", VisionDataType.Double),
                new("isGoodQuality", VisionDataType.Boolean),
                new("diagnostics", VisionDataType.String), new("warningCount", VisionDataType.Integer),
                Next
            ],
            [
                new("deviceId", "Device ID", "text", "virtual-modbus-1", Group: "Device", Description: "Non-empty device id registered in DeviceManager (max 128 chars)."),
                new("tagId", "Tag ID", "text", "statusText", Group: "Tag", Description: "Non-empty tag id on that device (max 128 chars)."),
                new("valueType", "Fallback Type", "select", "String", Options: [new("Boolean", "Boolean"), new("Integer", "Integer"), new("Double", "Double"), new("String", "String")], Group: "Fallback", Description: "How the fallback value text is parsed when no value input is connected: boolean, integer, double or string.") { OverriddenByPorts = ["value"] },
                new("fallbackValue", "Fallback Value", "text", "Vision OK", Group: "Fallback", Description: "Used only when the optional Any input is not connected.") { OverriddenByPorts = ["value"] },
                new("autoConnect", "Auto Connect", "boolean", true, Group: "Device", Description: "Connect the device first when it is disconnected.")
            ,






                ],
            "Write a typed device tag and read it back. Connected input overrides both fallback parameters. Int64 inputs retain their integer precision and numeric conversions reject non-finite values. Conversion validity ports describe the read-back sample.",
            "builtin",
            new VisionToolCapabilities(Deterministic: false, SupportsParallel: false, SupportsRunNode: false),
            Actions: [new(NodeActionKind.DeviceWrite, NodeActionCompletion.Synchronous)],
            Resources: [new("device", "deviceId")]),
        // ===== 自定义协议通信（2026-10-26 · 缺口分析 P0-7）：私有 TCP / UDP / 串口协议 =====
        new(
            "device.tcpRequest",
            "TCP Request",
            "Device",
            [ExecIn, new("payload", VisionDataType.String, Required: false)],
            [
                new("response", VisionDataType.String),
                new("responseHex", VisionDataType.String),
                new("bytesRead", VisionDataType.Integer),
                new("matched", VisionDataType.Boolean),
                new("elapsedMs", VisionDataType.Double),
                new("diagnostics", VisionDataType.String),
                Next
            ],
            [
                new("deviceId", "Device ID", "text", "tcp-1", Group: "Device", Description: "Logical device name used for the dispatch permit and audit trail (max 128 chars); it is not the network address."),
                new("host", "Host", "text", "127.0.0.1", Group: "Endpoint", Description: "Peer host name or IPv4 address."),
                new("port", "Port", "number", 5000, 1, 65535, 1, Group: "Endpoint", Description: "Peer TCP port (1-65535)."),
                new("timeoutMs", "Timeout", "number", 1000, 50, 60000, 50, Unit: "ms", Group: "Endpoint", Description: "Overall budget for connect + send + read. On expiry the connection is closed and the node fails; the request may already have reached the peer, so verify device state before retrying."),
                new("payload", "Request Payload", "textarea", "", Group: "Request", Description: "Bytes to send. Overridden by the payload input port. Compose dynamic payloads with value.string first.") { OverriddenByPorts = ["payload"] },
                new("payloadEncoding", "Payload Encoding", "select", "text", Options: [new("Text (UTF-8)", "text"), new("Hex", "hex")], Group: "Request", Description: "text sends the UTF-8 bytes of the payload; hex parses pairs such as '02 31 0D' (spaces, '-' and ':' are ignored)."),
                new("sendTerminator", "Send Terminator", "select", "none", Options: [new("None", "none"), new("CR", "cr"), new("LF", "lf"), new("CRLF", "crlf")], Group: "Request", Description: "Bytes appended after the payload."),
                new("readMode", "Read Mode", "select", "untilTerminator", Options: [new("Until terminator", "untilTerminator"), new("Fixed length", "fixedLength"), new("No reply", "none")], Group: "Response", Description: "How the reply is delimited. No reply sends the request and returns immediately."),
                new("readTerminator", "Read Terminator", "select", "crlf", Options: [new("CR", "cr"), new("LF", "lf"), new("CRLF", "crlf")], Group: "Response", VisibleWhen: new("readMode", ["untilTerminator"]), Description: "Reply terminator; it is stripped from the response before expectPattern is evaluated."),
                new("readLength", "Read Length", "number", 0, 0, 65535, 1, Group: "Response", VisibleWhen: new("readMode", ["fixedLength"]), Description: "Exact reply length in bytes; must be greater than 0 in fixed-length mode. A short reply fails instead of returning half a frame."),
                new("maxResponseBytes", "Max Response Bytes", "number", 4096, 16, 1048576, 1, Group: "Response", Description: "Hard reply cap. Exceeding it fails the node instead of silently truncating."),
                new("expectPattern", "Expect Pattern", "text", "", Group: "Validation", Description: "Optional .NET regular expression evaluated against the reply text (100 ms budget). Empty disables the check."),
                new("requireMatch", "Require Match", "boolean", false, Group: "Validation", Description: "When true a reply that does not match expectPattern fails the node; when false it only sets matched=false."),
                new("ignoreCase", "Ignore Case", "boolean", false, Group: "Validation", Description: "Case-insensitive expectPattern matching.")
                ],
            "Talk to devices that only speak a private TCP protocol (scanners, light controllers, torque tools). One connection per request: connect, send, read per readMode, close. The request may be binary (payloadEncoding=hex) and the reply is exposed both as UTF-8 text and as hex. Responses are capped by maxResponseBytes; timeouts close the connection and fail with a message that states the request may already have reached the peer. The dispatch is guarded by the device ownership permit for deviceId, so a released lease blocks the send.",
            "builtin",
            new VisionToolCapabilities(Deterministic: false, SupportsParallel: false, SupportsRunNode: false),
            Actions: [new(NodeActionKind.DeviceWrite, NodeActionCompletion.Synchronous)],
            Resources: [new("device", "deviceId")]),
        new(
            "device.udpMessage",
            "UDP Message",
            "Device",
            [ExecIn, new("payload", VisionDataType.String, Required: false)],
            [
                new("response", VisionDataType.String),
                new("responseHex", VisionDataType.String),
                new("bytesRead", VisionDataType.Integer),
                new("matched", VisionDataType.Boolean),
                new("elapsedMs", VisionDataType.Double),
                new("diagnostics", VisionDataType.String),
                Next
            ],
            [
                new("deviceId", "Device ID", "text", "udp-1", Group: "Device", Description: "Logical device name used for the dispatch permit and audit trail (max 128 chars)."),
                new("host", "Host", "text", "127.0.0.1", Group: "Endpoint", Description: "Peer host name or IPv4 address."),
                new("port", "Port", "number", 5001, 1, 65535, 1, Group: "Endpoint", Description: "Peer UDP port (1-65535)."),
                new("timeoutMs", "Timeout", "number", 1000, 50, 60000, 50, Unit: "ms", Group: "Endpoint", Description: "Budget for sending and for waiting for the reply datagram."),
                new("payload", "Payload", "textarea", "", Group: "Request", Description: "Datagram contents. Overridden by the payload input port.") { OverriddenByPorts = ["payload"] },
                new("payloadEncoding", "Payload Encoding", "select", "text", Options: [new("Text (UTF-8)", "text"), new("Hex", "hex")], Group: "Request", Description: "text sends UTF-8 bytes; hex parses pairs such as '02 31 0D'."),
                new("sendTerminator", "Send Terminator", "select", "none", Options: [new("None", "none"), new("CR", "cr"), new("LF", "lf"), new("CRLF", "crlf")], Group: "Request", Description: "Bytes appended after the payload."),
                new("expectReply", "Expect Reply", "boolean", true, Group: "Response", Description: "When true the node waits for one reply datagram within timeoutMs; when false it only sends."),
                new("expectPattern", "Expect Pattern", "text", "", Group: "Validation", Description: "Optional regular expression over the reply text; empty disables it."),
                new("requireMatch", "Require Match", "boolean", false, Group: "Validation", Description: "Fail when the reply does not match expectPattern instead of only reporting matched=false."),
                new("ignoreCase", "Ignore Case", "boolean", false, Group: "Validation", Description: "Case-insensitive expectPattern matching.")
                ],
            "Send one UDP datagram to a private-protocol device and optionally wait for a single reply datagram. Same payload encoding, terminator and expectPattern semantics as TCP Request, but no connection state: suitable for broadcast-style devices and simple ASCII protocols over Ethernet.",
            "builtin",
            new VisionToolCapabilities(Deterministic: false, SupportsParallel: false, SupportsRunNode: false),
            Actions: [new(NodeActionKind.DeviceWrite, NodeActionCompletion.Synchronous)],
            Resources: [new("device", "deviceId")]),
        new(
            "device.serialPort",
            "Serial Request",
            "Device",
            [ExecIn, new("payload", VisionDataType.String, Required: false)],
            [
                new("response", VisionDataType.String),
                new("responseHex", VisionDataType.String),
                new("bytesRead", VisionDataType.Integer),
                new("matched", VisionDataType.Boolean),
                new("elapsedMs", VisionDataType.Double),
                new("diagnostics", VisionDataType.String),
                Next
            ],
            [
                new("deviceId", "Device ID", "text", "serial-1", Group: "Device", Description: "Logical device name used for the dispatch permit and audit trail (max 128 chars)."),
                new("portName", "Port Name", "text", "COM1", Group: "Endpoint", Description: "Serial port name such as COM3."),
                new("baudRate", "Baud Rate", "number", 9600, 50, 4000000, 1, Group: "Endpoint", Description: "Baud rate (50-4000000)."),
                new("dataBits", "Data Bits", "number", 8, 5, 8, 1, Group: "Endpoint", Description: "Data bits (5-8)."),
                new("parity", "Parity", "select", "none", Options: [new("None", "none"), new("Odd", "odd"), new("Even", "even"), new("Mark", "mark"), new("Space", "space")], Group: "Endpoint", Description: "Parity mode."),
                new("stopBits", "Stop Bits", "select", "one", Options: [new("1", "one"), new("1.5", "onePointFive"), new("2", "two")], Group: "Endpoint", Description: "Stop bits."),
                new("dtrEnable", "DTR", "boolean", false, Group: "Endpoint", Description: "Assert DTR while the port is open; some RS-485 converters need it."),
                new("rtsEnable", "RTS", "boolean", false, Group: "Endpoint", Description: "Assert RTS while the port is open; no handshake is used."),
                new("timeoutMs", "Timeout", "number", 1000, 50, 60000, 50, Unit: "ms", Group: "Endpoint", Description: "Budget for write + read. On expiry the port is closed and the node fails; the command may already have reached the device."),
                new("payload", "Request Payload", "textarea", "", Group: "Request", Description: "Bytes to send. Overridden by the payload input port.") { OverriddenByPorts = ["payload"] },
                new("payloadEncoding", "Payload Encoding", "select", "text", Options: [new("Text (UTF-8)", "text"), new("Hex", "hex")], Group: "Request", Description: "text sends UTF-8 bytes; hex parses pairs such as '02 31 0D'."),
                new("sendTerminator", "Send Terminator", "select", "crlf", Options: [new("None", "none"), new("CR", "cr"), new("LF", "lf"), new("CRLF", "crlf")], Group: "Request", Description: "Bytes appended after the payload; CRLF matches most ASCII instruments."),
                new("readMode", "Read Mode", "select", "untilTerminator", Options: [new("Until terminator", "untilTerminator"), new("Fixed length", "fixedLength"), new("No reply", "none")], Group: "Response", Description: "How the reply is delimited."),
                new("readTerminator", "Read Terminator", "select", "crlf", Options: [new("CR", "cr"), new("LF", "lf"), new("CRLF", "crlf")], Group: "Response", VisibleWhen: new("readMode", ["untilTerminator"]), Description: "Reply terminator; stripped from the response."),
                new("readLength", "Read Length", "number", 0, 0, 65535, 1, Group: "Response", VisibleWhen: new("readMode", ["fixedLength"]), Description: "Exact reply length in bytes; must be greater than 0 in fixed-length mode."),
                new("maxResponseBytes", "Max Response Bytes", "number", 4096, 16, 1048576, 1, Group: "Response", Description: "Hard reply cap; exceeding it fails instead of truncating."),
                new("expectPattern", "Expect Pattern", "text", "", Group: "Validation", Description: "Optional regular expression over the reply text; empty disables it."),
                new("requireMatch", "Require Match", "boolean", false, Group: "Validation", Description: "Fail when the reply does not match expectPattern instead of only reporting matched=false."),
                new("ignoreCase", "Ignore Case", "boolean", false, Group: "Validation", Description: "Case-insensitive expectPattern matching.")
                ],
            "Talk to RS-232/485 devices that only speak a private ASCII or binary protocol (light controllers, handheld scanners, torque tools): open the port, send, read per readMode, close — the port is not held between requests so a stuck recipe cannot block the station. Same payload encoding, terminator and expectPattern semantics as TCP Request. UnauthorizedAccessException is reported as 'port held by another program'. Because the .NET serial stream ignores cancellation, replies are read byte by byte with ReadTimeout and a cancellation check between bytes.",
            "builtin",
            new VisionToolCapabilities(Deterministic: false, SupportsParallel: false, SupportsRunNode: false),
            Actions: [new(NodeActionKind.DeviceWrite, NodeActionCompletion.Synchronous)],
            Resources: [new("device", "deviceId")]),
        new(
            "device.waitTag",
            "Wait Device Tag",
            "Device",
            [ExecIn],
            [
                new("value", VisionDataType.DeviceTagValue),
                new("boolValue", VisionDataType.Boolean),
                new("boolValueValid", VisionDataType.Boolean),
                new("numberValue", VisionDataType.Double),
                new("numberValueValid", VisionDataType.Boolean),
                new("textValue", VisionDataType.String),
                new("quality", VisionDataType.String),
                new("timestamp", VisionDataType.String),
                new("matched", VisionDataType.Boolean),
                new("waitMs", VisionDataType.Double), new("diagnostics", VisionDataType.String), Next
            ],
            [
                new("deviceId", "Device ID", "text", "virtual-modbus-1", Group: "Device", Description: "Device identifier."),
                new("tagId", "Tag ID", "text", "trigger", Group: "Tag", Description: "Tag / PLC address to wait on."),
                new("operator", "Condition", "select", "True", Options: [new("True", "True"), new("False", "False"), new("==", "=="), new("!=", "!="), new(">", ">"), new(">=", ">="), new("<", "<"), new("<=", "<=")], Group: "Condition", Description: "Comparison operator for the wait condition."),
                new("compareValue", "Compare Value", "text", "1", Group: "Condition", Description: "Ignored for True/False. Integer tags compare in decimal arithmetic to preserve Int64 precision; double comparisons require finite numbers; text equality ignores case.", VisibleWhen: new("operator", ["==", "!=", ">", ">=", "<", "<="])),
                new("timeoutMs", "Timeout", "number", 5000, 50, 120000, 50, Unit: "ms", Group: "Wait", Description: "Timeout in milliseconds for the operation."),
                new("pollMs", "Poll Interval", "number", 25, 10, 5000, 5, Unit: "ms", Group: "Wait", Description: "Integer polling interval in milliseconds; cannot exceed Timeout."),
                new("autoConnect", "Auto Connect", "boolean", true, Group: "Device", Description: "Connect the device/robot automatically when needed."),
                new("triggerMode", "Trigger", "select", "Level", Options:
                [
                    new("Level", "Level"),
                    new("Rising", "Rising"),
                    new("Falling", "Falling")
                ], Group: "Condition", Description: "Level waits while the condition is true. Rising waits for false-to-true and Falling for true-to-false. The first sample cannot be an edge."),
                new("stableMs", "Stable Hold", "number", 0, 0, 120000, 1, Unit: "ms", Group: "Wait", Description: "The armed condition must stay true for this long. 0 accepts the first qualifying sample. Cannot exceed Timeout."),
                new("qualityMode", "Accepted Quality", "select", "Any", Options:
                [
                    new("Any", "Any"),
                    new("Good", "Good"),
                    new("Good or Uncertain", "GoodOrUncertain")
                ], Group: "Condition", Description: "Samples outside the accepted quality do not match, do not move the edge memory, and do not count toward the stable hold."),
                new("onTimeout", "On Timeout", "select", "Fail", Options:
                [
                    new("Fail the node", "Fail"),
                    new("Continue unmatched", "Continue")
                ], Group: "Wait", Description: "Fail throws the existing timeout error. Continue succeeds with matched=false so a downstream branch can handle the miss.")
            ,



                ],
            "Poll a device tag until a condition/edge has held for the requested duration or timeout. Elapsed time is independent of wall-clock changes. Integer comparisons preserve Int64 precision. Conversion validity ports describe the last sample. Timeout Continue returns matched=false; external cancellation still cancels.",
            "builtin",
            new VisionToolCapabilities(Deterministic: false, SupportsParallel: false),
            Resources: [new("device", "deviceId")]),
        new(
            "device.writeVisionResult",
            "Write Vision Result",
            "Device",
            [ExecIn, new("pass", VisionDataType.Boolean), new("x", VisionDataType.Double, Required: false), new("y", VisionDataType.Double, Required: false), new("r", VisionDataType.Double, Required: false)],
            [new("written", VisionDataType.Boolean), new("pass", VisionDataType.Boolean), new("x", VisionDataType.Double, Required: false), new("y", VisionDataType.Double, Required: false), new("r", VisionDataType.Double, Required: false),
                new("xWritten", VisionDataType.Boolean), new("yWritten", VisionDataType.Boolean), new("rWritten", VisionDataType.Boolean), new("tagsWritten", VisionDataType.Integer), new("diagnostics", VisionDataType.String), Next],
            [
                new("deviceId", "Device ID", "text", "virtual-modbus-1", Group: "Device", Description: "Non-empty device id registered in DeviceManager (max 128 chars)."),
                new("resultReadyTag", "Result Ready Tag", "text", "resultReady", Group: "Mapping", Description: "Ready/handshake tag written 0 before the payload and 1 after it."),
                new("resultOkTag", "Result OK Tag", "text", "resultOk", Group: "Mapping", Description: "Tag receiving the pass (OK/NG) boolean."),
                new("xTag", "X Tag", "text", "resultX", Group: "Mapping", Description: "Tag receiving the X coordinate; empty disables that write."),
                new("yTag", "Y Tag", "text", "resultY", Group: "Mapping", Description: "Tag receiving the Y coordinate; empty disables that write."),
                new("rTag", "R Tag", "text", "resultR", Group: "Mapping", Description: "Tag receiving the rotation R in degrees; empty disables that write."),
                new("autoConnect", "Auto Connect", "boolean", true, Group: "Device", Description: "Connect the device first when it is disconnected.")
            ,







                new("missingCoordinatePolicy", "Missing Coordinate Policy", "select", "skip", Group: "Coordinates", Options: [new("Skip Coordinate", "skip"), new("Fail Entire Write", "fail"), new("Write Zero", "zero")],
                    Description: "When a coordinate tag is set but its input is not connected, defaults to skip; an empty tag always disables the field. Only coordinates actually written produce x/y/r; xWritten/yWritten/rWritten report whether each coordinate was written."),




                ],
            "Writes in order Ready=0, OK/NG and enabled coordinates, Ready=1. An empty coordinate tag disables that field; a missing input is skipped, rejected, or written as zero according to the policy. Validation completes before writing and duplicate tags are rejected. tagsWritten counts actual write operations (including the two Ready writes), not distinct tags. The legacy payloadFormat/onOverflow parameters are no longer used.",
            "builtin",
            new VisionToolCapabilities(Deterministic: false, SupportsParallel: false, SupportsRunNode: false),
            Actions: [new(NodeActionKind.DeviceWrite, NodeActionCompletion.Synchronous)],
            Resources: [new("device", "deviceId")]),
        new(
            "robot.currentPose",
            "Robot Current Pose",
            "Robot",
            [ExecIn],
            [
                new("pose", VisionDataType.CoordinatePose2D),
                new("busy", VisionDataType.Boolean),
                new("inPosition", VisionDataType.Boolean),
                new("targetReady", VisionDataType.Boolean),
                new("execute", VisionDataType.Boolean),
                new("complete", VisionDataType.Boolean),
                new("handshakeError", VisionDataType.Boolean),
                new("ack", VisionDataType.Boolean),
                new("state", VisionDataType.String),
                new("connection", VisionDataType.String),
                new("error", VisionDataType.String),
                new("x", VisionDataType.Double),
                new("y", VisionDataType.Double),
                new("thetaDeg", VisionDataType.Double),
                new("hasError", VisionDataType.Boolean),
                new("diagnostics", VisionDataType.String), new("warningCount", VisionDataType.Integer), new("attemptCount", VisionDataType.Integer),
                Next
            ],
            [
                new("robotId", "Robot ID", "text", "virtual-abb-1", Group: "Robot", Description: "Non-empty robot id (max 128 chars)."),
                new("autoConnect", "Auto Connect", "boolean", true, Group: "Robot", Description: "Connect the robot automatically when the connection is down."),
                new("requireInPosition", "Require In Position", "boolean", false, Group: "Gates", Description: "Fail when the robot does not report InPosition."),
                new("requireNoError", "Require No Error", "boolean", false, Group: "Gates", Description: "Fail when the robot reports a handshake or device error."),
                new("expectedFrame", "Expected Frame (empty=any)", "text", "", Group: "Gates", Description: "When non-empty, fail if the reported pose frame does not match exactly.")
            ,






                ],
            "Read the current planar robot pose and neutral handshake state from RobotManager with id validation and diagnostics. Vendor protocol objects remain outside Workflow Core.",
            "builtin",
            new VisionToolCapabilities(Deterministic: false, SupportsParallel: false),
            Resources: [new("robot", "robotId")]),
        new(
            "robot.executeTarget",
            "Execute Robot Target",
            "Robot",
            [ExecIn, new("target", VisionDataType.RobotTarget2D)],
            [
                new("target", VisionDataType.RobotTarget2D),
                new("currentPose", VisionDataType.CoordinatePose2D),
                new("commandId", VisionDataType.Integer),
                new("inPosition", VisionDataType.Boolean),
                new("state", VisionDataType.String),
                new("traceId", VisionDataType.String),
                new("attempts", VisionDataType.Integer),
                new("diagnostics", VisionDataType.String), new("warningCount", VisionDataType.Integer), new("attemptCount", VisionDataType.Integer),
                Next
            ],
            [
                new("robotId", "Robot ID", "text", "virtual-abb-1", Group: "Robot", Description: "Robot device identifier."),
                new("action", "Action", "select", "Handshake", Options: [new("Industrial Handshake", "Handshake"), new("Move", "Move"), new("Send Target Only", "SendTarget")], Group: "Command", Description: "Robot motion / action verb."),
                new("autoConnect", "Auto Connect", "boolean", true, Group: "Command", Description: "Connect the device/robot automatically when needed."),
                new("waitForInPosition", "Wait For Complete", "boolean", true, Group: "Completion", VisibleWhen: new("action", ["Handshake", "Move"]), Description: "Block until the robot reports in-position."),
                new("timeoutMs", "Timeout", "number", 5000, 50, 120000, 50, Unit: "ms", Group: "Completion", VisibleWhen: new("action", ["Handshake", "Move"]), Description: "Timeout in milliseconds for the operation."),
                new("maxRetries", "Max Retries", "number", 1, 0, 10, 1, Group: "Handshake", VisibleWhen: new("action", ["Handshake"]), Description: "Maximum retries on transient failures."),
                new("retryDelayMs", "Retry Delay", "number", 100, 0, 10000, 10, Unit: "ms", Group: "Handshake", VisibleWhen: new("action", ["Handshake"]), Description: "Delay between retries in milliseconds."),
                new("autoAck", "Auto Ack", "boolean", true, Group: "Handshake", VisibleWhen: new("action", ["Handshake"]), Description: "Automatically acknowledge robot completion signals.")
            ,






                ],
            "Execute a vendor-neutral RobotTarget2D using SendTarget/Move or the standard TargetReady→Execute→Busy→Complete/Error→Ack industrial handshake with timeout/retry policy.",
            "builtin",
            new VisionToolCapabilities(Deterministic: false, SupportsParallel: false, SupportsRunNode: false),
            Actions: [new(NodeActionKind.RobotCommand, NodeActionCompletion.ProtocolConfirmed)],
            Resources: [new("robot", "robotId")]),
        new(
            "robot.guidance2d",
            "Robot Guidance 2D",
            "Robot",
            [ExecIn, new("pose", VisionDataType.CoordinatePose2D)],
            [new("target", VisionDataType.RobotTarget2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("r", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("diagnostics", VisionDataType.String), new("warningCount", VisionDataType.Integer), new("attemptCount", VisionDataType.Integer), Next],
            [
                new("robot", "Robot", "text", "ABB", Group: "Target", Description: "Robot identifier the generated target is intended for."),
                new("guidanceMode", "Guidance Mode", "select", "EyeToHand", Options: [new("Eye-to-Hand", "EyeToHand"), new("Eye-in-Hand", "EyeInHand"), new("Manual", "Manual")], Group: "Target", Description: "How the input pose is interpreted before offsets are applied."),
                new("expectedFrame", "Expected Frame", "text", "RobotBase", Group: "Target", Description: "Frame the input pose must already be resolved into; mismatch is rejected."),
                new("offsetX", "Pick Offset X", "number", 0, -10000, 10000, 0.001, Unit: "pose unit", Group: "Pick Offset", Description: "X offset added to the pose in the target frame/units."),
                new("offsetY", "Pick Offset Y", "number", 0, -10000, 10000, 0.001, Unit: "pose unit", Group: "Pick Offset", Description: "Y offset added to the pose in the target frame/units."),
                new("angleOffsetDeg", "Angle Offset", "number", 0, -3600, 3600, 0.001, Unit: "deg", Group: "Pick Offset", Description: "Rotation offset added to the pose angle in degrees.")
            ,



                new("frame", "Guidance Frame", "text", "", Group: "Target", Description: "Optional frame label stamped into diagnostics."),





                ],
            "Create a vendor-neutral RobotTarget2D from a pose already resolved into RobotBase (or another configured robot frame). Validates frame, finite pose/offsets and guidance mode; pick offset is applied in pose-local coordinates. Emits frame, unit and JSON diagnostics. Does not send motion to hardware.",
            "builtin"),
        new(
            "robot.j4TcpCompensation",
            "J4 / TCP Compensation",
            "Robot",
            [ExecIn, new("target", VisionDataType.RobotTarget2D)],
            [new("target", VisionDataType.RobotTarget2D), new("x", VisionDataType.Double), new("y", VisionDataType.Double), new("r", VisionDataType.Double), new("eccentricity", VisionDataType.Double), new("frame", VisionDataType.String), new("unit", VisionDataType.String), new("compensationMode", VisionDataType.String), new("diagnostics", VisionDataType.String), new("warningCount", VisionDataType.Integer), new("attemptCount", VisionDataType.Integer), Next],
            [
                new("tcpOffsetX", "TCP Offset X", "number", 0, -1000, 1000, 0.001, Unit: "target unit", Group: "Eccentricity", Description: "TCP offset X from the J4 rotation centre, in target units."),
                new("tcpOffsetY", "TCP Offset Y", "number", 0, -1000, 1000, 0.001, Unit: "target unit", Group: "Eccentricity", Description: "TCP offset Y from the J4 rotation centre, in target units."),
                new("j4PivotOffsetX", "J4 Pivot Offset X", "number", 0, -1000, 1000, 0.001, Unit: "target unit", Group: "Eccentricity", Description: "J4 pivot offset X in the same frame/units as the target."),
                new("j4PivotOffsetY", "J4 Pivot Offset Y", "number", 0, -1000, 1000, 0.001, Unit: "target unit", Group: "Eccentricity", Description: "J4 pivot offset Y in the same frame/units as the target."),
                new("angleZeroOffsetDeg", "J4 Angle Zero Offset", "number", 0, -360, 360, 0.001, Unit: "deg", Group: "Rotation", Description: "Angle added to the target rotation before compensation (degrees, normalised to -180..180)."),
                new("maxEccentricity", "Max Eccentricity (0=off)", "number", 0, 0, 10000, 0.001, Unit: "target unit", Group: "Eccentricity", Description: "Fail when TCP-to-pivot eccentricity exceeds this value; 0 disables the gate."),
                new("compensationMode", "Compensation Mode", "select", "tcpToPivot", Options: [new("TCP desired → J4 pivot command", "tcpToPivot"), new("J4 pivot desired → TCP command", "pivotToTcp")], Group: "Eccentricity", Description: "tcpToPivot converts a desired TCP pose into the J4 pivot command; pivotToTcp inverts that mapping."),
                new("angleUnit", "Angle Unit", "select", "deg", Options: [new("Degrees", "deg"), new("Radians", "rad")], Group: "Rotation", Description: "Unit of target.RDeg / angleZeroOffsetDeg; rad is converted to degrees before compensation.")
            ,

                new("maxAngleDeg", "Max |Angle| deg (0=off)", "number", 0, 0, 360, 0.1, Unit: "deg", Group: "Limits", Description: "Optional magnitude gate on compensation angle; 0 disables."),





                ],
            "Planar compensation for J4 rotation-center/TCP eccentricity: converts a desired TCP pose into the J4 pivot command pose using (TCP offset - pivot offset) rotated by the commanded angle. Validates all finite inputs; emits eccentricity magnitude and JSON diagnostics. Outputs a target only; it does not send motion to hardware.",
            "builtin"),
    ];
}
