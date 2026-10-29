using VisionStudio.Abstractions;

namespace VisionStudio.Engine;

/// <summary>
/// P0 补全批次的目录条目（2026-10-27）：JSON 组包/解析、表格读入、告警通知、跨轮计数。
/// 这四个缺口此前都必须靠 script.csharp 或上位机另写服务绕过。
/// </summary>
internal static class P0CompletionCatalog
{
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);

    internal static readonly IReadOnlyList<NodeCatalogItem> Items =
    [
        new("value.jsonBuild", "Build JSON", "Value",
            [Exec, new("a", VisionDataType.Any, Required: false), new("b", VisionDataType.Any, Required: false),
             new("c", VisionDataType.Any, Required: false), new("d", VisionDataType.Any, Required: false),
             new("e", VisionDataType.Any, Required: false), new("f", VisionDataType.Any, Required: false)],
            [
                new("json", VisionDataType.String), new("fieldCount", VisionDataType.Integer),
                new("bytes", VisionDataType.Integer), new("diagnostics", VisionDataType.String), Next
            ],
            [
                new("keys", "Field Keys", "text", "sn,result,x,y,z,ts", Group: "Fields",
                    Description: "Comma-separated key names in port order: the first key names a, the second b, and so on. Empty or '-' skips that port. Ignored in array mode."),
                new("mode", "Output Shape", "select", "object", Group: "Fields", Options:
                [
                    new("Object (key: value)", "object"),
                    new("Array (values in port order)", "array")
                ], Description: "Object is what MES/HTTP endpoints expect. Array keeps the order and drops the keys, which is handy for positional protocols."),
                new("pretty", "Pretty Print", "boolean", false, Group: "Fields",
                    Description: "Indent the JSON for logs and reports. Leave off for transport: every space costs bytes on a line running 60 parts per minute."),
                new("maxBytes", "Maximum Payload", "number", 65536, 64, 4194304, 1024, Group: "Limits",
                    Description: "Hard cap on the produced UTF-8 payload. Exceeding it fails instead of sending a truncated packet that the receiver will reject later."),
            ],
            "Pack up to six scalar values into one JSON document. Wire: a..f -> json/fieldCount/bytes. Number, integer, boolean and text are packed with their own JSON type; images, regions and detection lists are rejected so a payload never silently becomes a wall of internal fields. Duplicate or over-long keys fail. Use it for the classic 'SN + OK/NG + X/Y + timestamp' message that device.writeVisionResult cannot express. Text mode consumes the json port directly; for nested objects chain value.jsonParse/jsonBuild or script.csharp.",
            "builtin", new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: false, Deterministic: true),
            Actions: []),

        new("value.jsonParse", "Parse JSON", "Value",
            [Exec, new("json", VisionDataType.String, Required: false)],
            [
                new("found", VisionDataType.Boolean),
                new("value", VisionDataType.Any, Required: false),
                new("number", VisionDataType.Double, Required: false),
                new("text", VisionDataType.String, Required: false),
                new("boolean", VisionDataType.Boolean, Required: false),
                new("kind", VisionDataType.String), new("count", VisionDataType.Integer, Required: false),
                new("diagnostics", VisionDataType.String), Next
            ],
            [
                new("json", "JSON Text", "textarea", "{}", Group: "Source",
                    Description: "Fallback payload when the json port is disconnected. A connected json port wins.")
                    { OverriddenByPorts = ["json"] },
                new("path", "Path", "text", "$", Group: "Source",
                    Description: "Dot path with optional [n] indexes, for example data.items[0].code. $ or empty reads the whole document. Wildcards and filters are not supported on purpose: an industrial response is read, not queried."),
                new("type", "Value Type", "select", "auto", Group: "Result", Options:
                [
                    new("Auto (keep the JSON type)", "auto"),
                    new("Number", "number"),
                    new("Text", "text"),
                    new("Boolean", "boolean")
                ], Description: "Auto returns text/number/boolean as-is and gives objects/arrays back as raw JSON text (parse them again). Forcing number or boolean converts numeric/boolean-looking text and fails when it cannot."),
                new("required", "Require The Path", "boolean", true, Group: "Result",
                    Description: "When the path is missing: true fails the run (default, a silent null always explodes three nodes later); false returns found=false for flow.if to branch on."),
                new("maxBytes", "Maximum Payload", "number", 1048576, 64, 16777216, 1024, Group: "Result",
                    Description: "Reject oversized payloads before parsing. A 100 MB HTML error page should not become a JSON parse attempt."),
            ],
            "Read one field out of a JSON response. Wire: json -> found/value/number/text/boolean/kind/count. Path syntax is $.a.b[0].c; objects and arrays come back as raw JSON text so a second node can drill further. Missing paths fail by default, and forcing a type that the value cannot represent also fails instead of returning null. Failures: empty input, invalid JSON, payload above maxBytes.",
            "builtin", new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: false, Deterministic: true),
            Actions: []),

        new("device.readTable", "Read Table Row", "Device",
            [Exec, new("path", VisionDataType.String, Required: false),
             new("sequence", VisionDataType.Integer, Required: false)],
            [
                new("found", VisionDataType.Boolean), new("index", VisionDataType.Integer),
                new("total", VisionDataType.Integer), new("cellCount", VisionDataType.Integer),
                new("header", VisionDataType.String), new("columnsJson", VisionDataType.String),
                new("row", VisionDataType.String), new("rowJson", VisionDataType.String),
                new("rowObjectJson", VisionDataType.String, Required: false),
                new("diagnostics", VisionDataType.String), Next
            ],
            [
                new("path", "Table File", "text", string.Empty, Group: "Source",
                    Description: "CSV/TSV file to read, relative to an authorized read root. Reads go through the host IReplayInputResolver, so arbitrary paths are rejected.")
                    { OverriddenByPorts = ["path"] },
                new("delimiter", "Delimiter", "select", "auto", Group: "Source", Options:
                [
                    new("Auto (from the header line)", "auto"),
                    new("Comma", "comma"),
                    new("Tab", "tab"),
                    new("Semicolon", "semicolon"),
                    new("Pipe", "pipe")
                ], Description: "Auto counts each candidate in the first line and keeps the widest split. Set it explicitly when a column legitimately contains commas in quotes."),
                new("encoding", "Encoding", "select", "utf8", Group: "Source", Options:
                [
                    new("UTF-8 (with BOM detection)", "utf8"),
                    new("GB18030 (Excel China default)", "gb18030"),
                    new("Latin-1", "latin1")
                ], Description: "Excel on a Chinese Windows saves CSV as GBK; read it as gb18030 or the SN column comes back as mojibake."),
                new("hasHeader", "First Row Is Header", "boolean", true, Group: "Source",
                    Description: "When on, row 0 becomes the column names and is excluded from the data rows; rowObjectJson is only produced when the header width matches the row width."),
                new("index", "Row Index", "number", 0, 0, 1000000, 1, Group: "Selection",
                    Description: "0-based data row to read when no sequence input is connected. Connect sequence to drive one row per part."),
                new("wrap", "Wrap Around", "boolean", false, Group: "Selection",
                    Description: "Cycle back to row 0 after the last row. Lines that run the whole table repeatedly usually want this on; a fixed run wants it off so an exhausted table fails loudly."),
                new("maxRows", "Maximum Rows", "number", 100000, 1, 1000000, 1000, Group: "Limits",
                    Description: "Refuse tables above this many rows instead of silently truncating the list the line is indexing into."),
                new("maxMegabytes", "Maximum File Size", "number", 16, 1, 512, 1, Unit: "MB", Group: "Limits",
                    Description: "Refuse files above this size before reading. A production table is kilobytes; a runaway log should not be swallowed as a table."),
            ],
            "Read one data row out of a CSV/TSV table so the flow can be driven by a work order list (one SN per part). Wire: path + optional sequence -> row/rowJson/rowObjectJson plus index/total for loop control. Parsing follows RFC4180 quoting, empty lines are skipped, and the parsed table is cached by (path, mtime, size, encoding, delimiter) so a batch run parses the file once. Reading requires host authorization (IReplayInputResolver). Out-of-range index with wrap off returns found=false. Deterministic=false: the file may change between runs.",
            "builtin", new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: false, Deterministic: false),
            Actions: []),

        new("output.notify", "Send Notification", "Output",
            [Exec, new("message", VisionDataType.String, Required: false),
             new("value", VisionDataType.Any, Required: false),
             new("url", VisionDataType.String, Required: false)],
            [
                new("delivered", VisionDataType.Boolean), new("attempts", VisionDataType.Integer),
                new("truncated", VisionDataType.Boolean), new("statusCode", VisionDataType.Integer, Required: false),
                new("responseBody", VisionDataType.String, Required: false),
                new("diagnostics", VisionDataType.String), Next
            ],
            [
                new("channel", "Channel", "select", "webhook", Group: "Channel", Options:
                [
                    new("Webhook (structured JSON POST)", "webhook"),
                    new("WeCom group robot (text)", "wecom"),
                    new("SMTP mail", "smtp")
                ], Description: "Webhook for MES or your own alarm platform, WeCom for the on-call group chat, SMTP when quality or maintenance needs a written record."),
                new("url", "Webhook URL", "text", string.Empty, Group: "Channel",
                    VisibleWhen: new("channel", ["webhook", "wecom"]),
                    Description: "Absolute http/https endpoint. A connected url port overrides it, so the address can come from a recipe or a device tag."),
                new("subject", "Subject", "text", "VisionStudio alarm", Group: "Message",
                    Description: "First line of the notification and the mail subject. Keep it short: the on-call engineer reads the subject on a lock screen."),
                new("message", "Message", "textarea", string.Empty, Group: "Message",
                    Description: "Body text. A connected message port overrides it. Empty body plus a value input still sends the serialized value.")
                    { OverriddenByPorts = ["message"] },
                new("failOnError", "Fail When Undelivered", "boolean", true, Group: "Delivery",
                    Description: "Fail the run when the notification cannot be delivered (default). Silent success makes 'the alarm was sent' a false claim that only surfaces during the post-mortem."),
                new("timeoutMs", "Timeout", "number", 5000, 200, 120000, 100, Unit: "ms", Group: "Delivery",
                    Description: "Per-attempt timeout. A notification must never hold the line: keep it well below the takt time."),
                new("retries", "Retries", "number", 1, 0, 5, 1, Group: "Delivery",
                    Description: "Extra attempts for recoverable failures only (timeout, connection error, 5xx). 4xx is a configuration error and never retried."),
                new("retryDelayMs", "Retry Delay", "number", 500, 0, 10000, 100, Unit: "ms", Group: "Delivery",
                    Description: "Wait between attempts so a briefly unavailable endpoint has time to come back."),
                new("maxPayloadBytes", "Maximum Payload", "number", 32768, 256, 1048576, 1024, Group: "Delivery",
                    Description: "Reject oversized notifications before sending. WeCom text is additionally capped at 2048 bytes and truncated with truncated=true."),
                new("smtpHost", "SMTP Host", "text", string.Empty, Group: "SMTP",
                    VisibleWhen: new("channel", ["smtp"]), Description: "Mail server host name or IP."),
                new("smtpPort", "SMTP Port", "number", 587, 1, 65535, 1, Group: "SMTP",
                    VisibleWhen: new("channel", ["smtp"]), Description: "Submission port: 587 with STARTTLS is the common default, 465 for implicit TLS."),
                new("smtpFrom", "From", "text", string.Empty, Group: "SMTP",
                    VisibleWhen: new("channel", ["smtp"]), Description: "Sender address. Many servers reject a From that does not match the authenticated account."),
                new("smtpTo", "To", "text", string.Empty, Group: "SMTP",
                    VisibleWhen: new("channel", ["smtp"]), Description: "Recipients separated by comma or semicolon."),
                new("smtpUser", "SMTP User", "text", string.Empty, Group: "SMTP",
                    VisibleWhen: new("channel", ["smtp"]), Description: "Login name. Leave empty only for an anonymous relay inside the plant network."),
                new("smtpPassword", "SMTP Password", "text", string.Empty, Group: "SMTP",
                    VisibleWhen: new("channel", ["smtp"]),
                    Description: "Password for the SMTP account. It is stored in plain text inside the workflow, so keep such recipes out of shared exports and restrict who can edit them; an anonymous relay inside the plant network avoids the problem entirely."),
                new("smtpSsl", "SMTP TLS", "boolean", true, Group: "SMTP",
                    VisibleWhen: new("channel", ["smtp"]), Description: "Enable TLS. Turn off only for a local relay that does not offer it."),
            ],
            "Send an alarm out of the flow: Webhook (structured JSON POST), WeCom group robot (text message) or SMTP mail. Wire: optional message/value/url -> delivered/attempts/statusCode. A connected value is serialized into the webhook payload (images degrade to width/height). Only recoverable failures are retried; 4xx fails immediately. Undelivered notifications fail the run unless failOnError is off. This node declares an external side effect, so debug runs require approval. Failures: bad URL, missing SMTP fields, payload above maxPayloadBytes, or an undelivered message.",
            "builtin", new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: false, Deterministic: false, SupportsRunNode: false),
            Actions: [new(NodeActionKind.ExternalCall, NodeActionCompletion.Synchronous)]),

        new("value.counter", "Run Counter", "Value",
            [Exec, new("pass", VisionDataType.Boolean, Required: false),
             new("reset", VisionDataType.Boolean, Required: false)],
            [
                new("count", VisionDataType.Integer), new("total", VisionDataType.Integer),
                new("passCount", VisionDataType.Integer), new("failCount", VisionDataType.Integer),
                new("rate", VisionDataType.Double), new("diagnostics", VisionDataType.String), Next
            ],
            [
                new("counterId", "Counter Id", "text", "counter", Group: "Counter",
                    Description: "Name of this counter inside its scope. Two nodes sharing the id share state, which is exactly how 'consecutive NG' and 'shift total' are wired on the same branch."),
                new("step", "Step", "number", 1, -1000000, 1000000, 1, Group: "Counter",
                    Description: "Added to count on every execution; use -1 for a remaining-parts countdown."),
                new("scope", "Scope", "select", "workflow", Group: "Counter", Options:
                [
                    new("Workflow (survives across runs)", "workflow"),
                    new("Run (resets every run)", "run")
                ], Description: "Workflow keeps counting across parts for shift yield and consecutive-NG rules. Run restarts with every run and suits per-part sampling."),
                new("resetOnPass", "Reset On Pass", "boolean", false, Group: "Reset",
                    Description: "Clear the counter when the pass input is true, then count this execution as the first one. This is the reset point of a consecutive-NG counter."),
                new("resetOnFail", "Reset On Fail", "boolean", false, Group: "Reset",
                    Description: "Clear the counter when the pass input is false, then count this execution. Use it for a consecutive-OK or no-fault streak."),
            ],
            "Cross-run counter with yield statistics. Wire: optional pass + reset -> count/total/passCount/failCount/rate. Reset is evaluated before incrementing, so the part that triggers the reset counts as the first item, not the zeroth. State lives in the host counter store keyed by workflow (default) or run, is thread-safe for parallel branches, and is snapshotted to data/runtime/counters.json every 5s so shift totals survive a restart; the node itself requires the store to be registered and fails fast when it is missing. Deterministic=false because the result depends on execution history.",
            "builtin", new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: false, Deterministic: false),
            Actions: [])
    ];
}
