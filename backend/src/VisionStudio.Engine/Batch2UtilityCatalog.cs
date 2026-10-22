using VisionStudio.Abstractions;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine;

/// <summary>Directory entries for batch-two utility/flow/output executors. Host registration remains explicit.</summary>
public static class Batch2UtilityCatalog
{
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);

    public static IReadOnlyList<NodeCatalogItem> Items { get; } =
    [
        new("flow.delay", "Delay", "Flow", [Exec], [new("elapsedMs", VisionDataType.Double), new("requestedMs", VisionDataType.Integer), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("preview", VisionDataType.Image, Required: false), new("cancelled", VisionDataType.Boolean), new("startedUtc", VisionDataType.String), new("finishedUtc", VisionDataType.String), Next],
            [new("delayMs", "Delay", "number", 1000, 0, 600000, 10, Unit: "ms", Description: "Requested wait in milliseconds (0-600000); cancellation stops the wait immediately."),
                new("maxDelayMs", "Max Delay ms", "number", 600000, 1, 600000, 1, Description: "Hard upper bound rejected at configure/runtime for delayMs."),
                new("precision", "Timing Precision", "select", "standard", Options: [new("Standard", "standard"), new("High-res spin", "hires")], Description: "Whether to spin-wait the final millisecond for tighter elapsedMs."),
                ],
            "Wait for the configured duration; cancellation stops the wait immediately. Reports requested and measured elapsed time in JSON diagnostics; out-of-range delays fail deterministically.", "builtin",
            new VisionToolCapabilities(SupportsParallel: false, ExecutionMode: VisionExecutionMode.WorkflowCore), Actions: []),
        new("flow.ifBoolean", "Boolean If", "Flow", [Exec, new("condition", VisionDataType.Boolean)],
            [new("true", VisionDataType.Control), new("false", VisionDataType.Control), new("condition", VisionDataType.Boolean), new("branch", VisionDataType.String), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("elapsedMs", VisionDataType.Double), new("label", VisionDataType.String)], [new("trueLabel", "True Branch Label", "text", "true", Description: "Label recorded in diagnostics when the boolean is true."),
                new("falseLabel", "False Branch Label", "text", "false", Description: "Label recorded in diagnostics when the boolean is false."),
                new("strictLabels", "Strict Labels", "boolean", true, Description: "Reject blank branch labels."),
                ],
            "Choose the true or false path from the Boolean condition input, then converge the paths at a Join. Also emits the condition, the selected branch name (true/false) and JSON diagnostics for run tracing.", "builtin",
            new VisionToolCapabilities(ExecutionMode: VisionExecutionMode.WorkflowCore), Actions: []),
        new("flow.merge", "Flow Merge", "Flow",
            Enumerable.Range(1, 16).Select(index => new PortDescriptor($"case{index}", VisionDataType.Control, Required: false))
                .Append(new PortDescriptor("default", VisionDataType.Control, Required: false)).ToArray(),
            [Next, new("merged", VisionDataType.Boolean), new("nodeId", VisionDataType.String), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("preview", VisionDataType.Image, Required: false), new("elapsedMs", VisionDataType.Double)], [new("policy", "Merge Policy", "select", "any", Internal: true, Options: [new("Any ready", "any"), new("All ready", "all")], Description: "Documented merge readiness policy for host scheduling."),

                ], "Converge selected If/Switch branches. A real incoming branch is required by structured workflow compilation. Emits a JSON diagnostics marker so merged runs remain traceable in node summaries.", "builtin",
            new VisionToolCapabilities(ExecutionMode: VisionExecutionMode.WorkflowCore), Actions: []),
        new("flow.try", "Try / Catch", "Flow", [Exec],
            [new("body", VisionDataType.Control), new("success", VisionDataType.Control), new("error", VisionDataType.Control), new("succeeded", VisionDataType.Boolean), new("errorMessage", VisionDataType.String), new("attempts", VisionDataType.Integer), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("preview", VisionDataType.Image, Required: false), new("elapsedMs", VisionDataType.Double), new("retried", VisionDataType.Boolean)],
            [new("maxRetries", "Max Retries", "number", 0, 0, 3, 1, Group: "Retry", Description: "Automatic retries for eligible deterministic compute bodies only; cancellation is never retried."), new("retryDelayMs", "Retry Delay", "number", 100, 0, 5000, 10, Unit: "ms", Group: "Retry", Description: "Delay between retry attempts in milliseconds."),
                new("failurePolicy", "Body Failure Policy", "select", "route", Group: "Failure", Options: [new("Route to error branch", "route"), new("Stop workflow", "rethrow")], Description: "Route failed bodies to error, or terminate after retries are exhausted."),
                new("captureStack", "Capture Stack Trace", "boolean", false, Group: "Failure", Description: "When true, include exception stack traces in diagnostics on failure."),
                new("failOnWarning", "Fail On Warning", "boolean", false, Group: "Failure", Description: "Fail the try body when the body node reports warningCount greater than zero. Implemented by the workflow runtime."),
                new("rethrowPolicy", "Rethrow Policy", "select", "never", Internal: true, Options: [new("Never", "never"), new("Unhandled only", "unhandled")], Description: "Whether caught exceptions are rethrown after diagnostics."),

                ],
            "Catch body failures and route to success or error. Automatic retries are limited to linear deterministic built-in Pipeline compute nodes with no declared actions; cancellation is never retried. Connect body/success to merge case1/case2; route mode also requires error/default. Stop mode permits omitting the error branch.", "builtin",
            new VisionToolCapabilities(Deterministic: false, SupportsRunNode: false, SupportsParallel: false, ExecutionMode: VisionExecutionMode.WorkflowCore), Actions: []),
        new("flow.switch", "Switch", "Flow", [Exec, new("value", VisionDataType.Any)],
            [new("selected", VisionDataType.String), new("matched", VisionDataType.Boolean), new("caseCount", VisionDataType.Integer), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("preview", VisionDataType.Image, Required: false), new("selectedCase", VisionDataType.String), new("elapsedMs", VisionDataType.Double), new("case1", VisionDataType.Control), new("case2", VisionDataType.Control), new("case3", VisionDataType.Control), new("case4", VisionDataType.Control), new("case5", VisionDataType.Control), new("case6", VisionDataType.Control), new("case7", VisionDataType.Control), new("case8", VisionDataType.Control), new("case9", VisionDataType.Control), new("case10", VisionDataType.Control), new("case11", VisionDataType.Control), new("case12", VisionDataType.Control), new("case13", VisionDataType.Control), new("case14", VisionDataType.Control), new("case15", VisionDataType.Control), new("case16", VisionDataType.Control), new("default", VisionDataType.Control)],
            [new("casesJson", "Cases (JSON string array)", "textarea", SwitchNode.DefaultCasesJson, Description: "JSON array of 1-16 non-empty unique case labels; malformed JSON or duplicates fail deterministically."),

                ],
            "Choose one matching path. casesJson entries map in order to case1-caseN; only configured case ports are exposed on the node. Unmatched values take the default path and report matched=false. Converge all configured paths at Flow Merge. Emits case count and JSON diagnostics.", "builtin",
            new VisionToolCapabilities(ExecutionMode: VisionExecutionMode.WorkflowCore), Actions: []),
        new("output.saveImage", "Save Image", "Output", [Exec, new("image", VisionDataType.Image), new("path", VisionDataType.String, Required: false),
                new("pass", VisionDataType.Boolean, Required: false) { RequiredWhen = new("resultMode", ["byResult", "okOnly", "ngOnly"]) },
                new("partId", VisionDataType.String, Required: false), new("sequence", VisionDataType.Integer, Required: false)],
            [new("path", VisionDataType.String), new("saved", VisionDataType.Boolean), new("bytesWritten", VisionDataType.Integer), new("format", VisionDataType.String), new("imageWidth", VisionDataType.Integer), new("imageHeight", VisionDataType.Integer),
                new("reason", VisionDataType.String), new("runId", VisionDataType.String), new("attemptCount", VisionDataType.Integer), new("warningCount", VisionDataType.Integer), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), Next],
            [new("path", "Image Path", "text", "artifacts/output.png", Group: "归档命名", Description: "Base file path; the extension selects the encoding. A wired path takes precedence. Timestamp and template modes replace the file-name part and use the directory as the archive root.") { VariantParameter = "namingMode", Variants = new Dictionary<string, ParameterPresentation> {
                ["fixed"] = new(Label: "Full File Path", Description: "完整目标文件路径：目录、文件名和扩展名都会使用。"),
                ["timestamp"] = new(Label: "Base Path (name + timestamp)", Description: "目录与扩展名取自该路径；文件名部分作为前缀，再追加时间戳和保存标识。"),
                ["template"] = new(Label: "Base Path (extension only)", Description: "目录与扩展名取自该路径；文件名完全由模板生成，路径中的文件名被忽略。")
             }, OverriddenByPorts = ["path"] },
             new("namingMode", "File Naming", "select", "timestamp", Options: [new("Timestamp and Save ID", "timestamp"), new("File Name Template", "template"), new("Fixed File Name", "fixed")], Group: "归档命名", Description: "New nodes append the host timestamp and a save id to the base name so consecutive saves do not collide. Legacy workflows keep the fixed file name; retries reuse the same save id."),
             new("filenameTemplate", "File Name Template", "text", "{timestamp}_{saveId}", Group: "归档命名", VisibleWhen: new("namingMode", ["template"]), Description: "Without directory or extension. Supports {timestamp}/{date}/{runId}/{saveId}/{nodeId}/{result}/{partId}/{sequence}; result requires pass and sequence requires a non-negative integer input padded to at least 6 digits."),
             new("partId", "Default Part ID", "text", "", Group: "归档命名", VisibleWhen: new("namingMode", ["template"]), Description: "Used only when the template contains {partId}; a wired partId takes precedence. Invalid file-name characters are replaced and a content fingerprint is appended so distinct ids never collapse into the same name.") { OverriddenByPorts = ["partId"] },
             new("splitByDate", "Split by Date", "boolean", false, Group: "归档命名", Description: "Appends a yyyy-MM-dd folder using the host's local time; combined with result classification the folder becomes Date/OK or Date/NG."),
             new("resultMode", "Save Scope", "select", "all", Options: [new("Save All, No Result Folders", "all"), new("Save All, Split OK/NG Folders", "byResult"), new("Save OK Only", "okOnly"), new("Save NG Only", "ngOnly")], Group: "归档命名", Description: "Classification and filtering require a wired pass input. OK/NG-only modes still use the matching result folder; filtered-out images return saved=false without encoding, creating files or creating directories."),
             new("conflictPolicy", "Conflict Policy", "select", "suffix", Options: [new("Save as Unique File", "suffix"), new("Fail", "fail"), new("Skip", "skip"), new("Overwrite", "overwrite")], Group: "归档命名", Description: "Unique-name mode appends a unique id and keeps no global counter. Conflicts are re-checked atomically at publish time; only explicit overwrite replaces an existing file."),
             new("overwrite", "Overwrite Existing", "boolean", false, Internal: true, Group: "归档命名", Description: "Legacy overwrite alias, honored only when conflictPolicy is absent. New configurations use Conflict Policy exclusively."),
                new("createDirectories", "Create Directories", "boolean", true, Group: "归档命名", Description: "Create missing parent directories before saving."),
                new("format", "Force Format", "select", "auto", Options: [new("From extension", "auto"), new("PNG", "png"), new("JPEG", "jpeg"), new("BMP", "bmp")], Group: "编码与像素", Description: "Select the output codec; the path extension must agree. Auto uses the extension."),
                new("jpegQuality", "JPEG Quality", "number", 92, 1, 100, 1, Group: "编码与像素", VisibleWhen: new("format", ["auto", "jpeg"]), Description: "JPEG quality when saving .jpg/.jpeg."),
                new("pngCompression", "PNG Compression", "number", 3, 0, 9, 1, Group: "编码与像素", VisibleWhen: new("format", ["auto", "png"]), Description: "PNG compression level 0..9 when saving .png."),
                new("colorMode", "Color Mode", "select", "asIs", Options: [new("As-is", "asIs"), new("Force Gray", "gray"), new("Force BGR", "bgr")], Group: "编码与像素", Description: "Optional color conversion before encoding."),
                new("pixelPolicy", "Pixel Policy", "select", "asIs", Options: [new("As-is", "asIs"), new("8-bit", "u8"), new("16-bit", "u16")], Group: "编码与像素", Description: "Saturating depth conversion before encoding, without intensity normalization."),
                new("maxWidth", "Max Width (0=off)", "number", 0, 0, 32768, 1, Group: "尺寸限制", Description: "Optional max width; larger images fail rather than silently resize; 0 disables."),
                new("maxHeight", "Max Height (0=off)", "number", 0, 0, 32768, 1, Group: "尺寸限制", Description: "Optional max height; 0 disables."),
                new("failOnEmpty", "Fail On Empty Image", "boolean", true, Group: "失败与诊断", Description: "Fail when the input image is empty."),
                new("diagnosticsLevel", "Diagnostics Level", "select", "standard", Options: [new("Minimal", "minimal"), new("Standard", "standard"), new("Verbose", "verbose")], Group: "失败与诊断", Description: "Controls how many fields are included in the diagnostics JSON payload."),
                new("retryPolicy", "Retry Policy", "select", "none", Options: [new("None", "none"), new("Once", "once"), new("Backoff", "backoff")], Group: "失败与诊断", Description: "Retries only temporary-file writes before atomic publish and publish-time IO errors; encoding, directory creation and parameter errors fail immediately.")],
            "Supports continuous image archiving: timestamp/template/fixed naming, date and OK/NG folders, and error/overwrite/unique-name/skip conflict handling. path/partId can be wired, sequence comes from upstream, and runId is this workflow run's identity. Files are published atomically and the actual path is returned; when saved=false, reason distinguishes the skip cause. Legacy workflows keep fixed-path and overwrite semantics.", "builtin",
            new VisionToolCapabilities(Deterministic: false, SupportsRunNode: false, SupportsParallel: false, ExecutionMode: VisionExecutionMode.WorkflowCore), Actions: []),
        new("output.exportResult", "Export Result", "Output", [Exec, new("value", VisionDataType.Any)],
            [new("path", VisionDataType.String), new("bytesWritten", VisionDataType.Integer), new("format", VisionDataType.String), new("valueType", VisionDataType.String), new("preview", VisionDataType.Image, Required: false), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("warningCount", VisionDataType.Integer), new("attemptCount", VisionDataType.Integer), Next],
            [new("path", "Result Path", "text", "artifacts/result.json", Group: "Destination", Description: "Absolute or host-relative destination path for the serialized result."),
             new("format", "Format", "select", "json", Group: "Format", Options: [new("JSON", "json"), new("CSV", "csv")], Description: "Serialization format: indented JSON object or a quoted CSV row."),
             new("overwrite", "Overwrite Existing", "boolean", false, Internal: true, Description: "Legacy alias. Replace an existing file at the target path instead of failing."),
             new("maxBytes", "Max Payload Bytes", "number", 10485760, 64, 67108864, 1024, Unit: "bytes", Group: "Limits", Description: "Fail before writing when the serialized payload exceeds this budget."),
                new("pretty", "Pretty JSON", "boolean", false, Group: "Format", VisibleWhen: new("format", ["json"]), Description: "Pretty-print JSON exports when format=json."),
                new("encoding", "Text Encoding", "select", "utf8", Group: "Format", Options: [new("UTF-8", "utf8"), new("UTF-8 BOM", "utf8bom")], Description: "Text encoding for file exports."),
                new("createDirectories", "Create Directories", "boolean", true, Group: "Destination", Description: "Create missing parent directories before export."),
                new("appendNewline", "Append Trailing Newline", "boolean", true, Group: "Format", Description: "Ensure text exports end with a newline."),
                new("conflictPolicy", "Conflict Policy", "select", "fail", Group: "Destination", Options: [new("Overwrite", "overwrite"), new("Fail", "fail"), new("Suffix", "suffix")], Description: "Behavior when the destination path already exists."),
                new("newline", "Newline Style", "select", "lf", Group: "Format", Options: [new("LF", "lf"), new("CRLF", "crlf")], Description: "Newline style for text exports."),


                new("diagnosticsLevel", "Diagnostics Level", "select", "standard", Group: "Diagnostics", Options: [new("Minimal", "minimal"), new("Standard", "standard"), new("Verbose", "verbose")], Description: "Controls how many fields are included in the diagnostics JSON payload."),
                new("retryPolicy", "Retry Policy", "select", "none", Group: "Diagnostics", Options: [new("None", "none"), new("Once", "once"), new("Backoff", "backoff")], Description: "Optional soft retry when the first attempt hits a transient IO/timeout warning; does not mask hard validation failures.")],
            "Write one typed result value to JSON or a quoted CSV row with an atomic temp-file commit, payload size gate, and diagnostics.", "builtin",
            new VisionToolCapabilities(Deterministic: false, SupportsRunNode: false, SupportsParallel: false, ExecutionMode: VisionExecutionMode.WorkflowCore), Actions: []),
        new("output.httpRequest", "HTTP Request", "Output", [Exec, new("url", VisionDataType.String, Required: false), new("body", VisionDataType.String, Required: false), new("value", VisionDataType.Any, Required: false), new("idempotencyKey", VisionDataType.String, Required: false)],
            [new("statusCode", VisionDataType.Integer), new("body", VisionDataType.String), new("success", VisionDataType.Boolean), new("attempts", VisionDataType.Integer), new("responseLength", VisionDataType.Integer), new("elapsedMs", VisionDataType.Double), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("warningCount", VisionDataType.Integer), new("attemptCount", VisionDataType.Integer), Next],
            [new("url", "URL", "text", "", Group: "Request", Description: "Absolute http/https address; a wired url input takes precedence over this parameter.") { OverriddenByPorts = ["url"] }, new("method", "Method", "select", "GET", Group: "Request", Options: [new("GET", "GET"), new("POST", "POST"), new("PUT", "PUT"), new("DELETE", "DELETE"), new("HEAD", "HEAD")], Description: "HTTP method: GET, POST, PUT, DELETE or HEAD."),
             new("body", "Request Body", "textarea", "", Group: "Request", VisibleWhen: new("method", ["POST", "PUT", "DELETE"]), Description: "A wired body input is sent verbatim; a wired value input is serialized as JSON with application/json. Do not connect both; wiring takes precedence over this parameter. GET/HEAD send no body.") { OverriddenByPorts = ["body", "value"] },
             new("contentType", "Content Type", "text", "application/json", Group: "Request", VisibleWhen: new("method", ["POST", "PUT", "DELETE"]), Description: "Content-Type for a raw body; a wired value input always uses application/json.") { OverriddenByPorts = ["value"] },
             new("maxRequestBytes", "Max Request Bytes", "number", 1048576, 1, 67108864, 1024, Unit: "bytes", Group: "Request", VisibleWhen: new("method", ["POST", "PUT", "DELETE"]), Description: "Validated against the UTF-8 byte count before sending; oversized requests are rejected."),
             new("timeoutMs", "Timeout", "number", 10000, 1, 120000, 100, Unit: "ms", Group: "Response", Description: "Per-attempt timeout in milliseconds."),
             new("maxResponseBytes", "Response Size Limit", "number", 1048576, 1, 67108864, 1024, Unit: "bytes", Group: "Response", Description: "Maximum response body bytes; larger responses fail explicitly without returning truncated content."),
             new("retries", "Retries", "number", 0, 0, 5, 1, Group: "Retry", Description: "Extra attempts for GET/HEAD on transport errors, timeouts, 5xx, 408 and 429. POST/PUT/DELETE do not retry by default; enable retries explicitly and supply an idempotency key."),
             new("retryWriteRequests", "Retry Write Requests", "boolean", false, Group: "Retry", VisibleWhen: new("method", ["POST", "PUT", "DELETE"]), Description: "When enabled with retries>0 an idempotency key is required and kept identical across retries. The server must deduplicate Idempotency-Key; a service that ignores the header may execute the request more than once."),
             new("idempotencyKey", "Idempotency Key", "text", "", Group: "Retry", Description: "A wired idempotencyKey takes precedence; at most 256 printable ASCII characters without spaces. Use a stable unique id per product/business operation; reuse it for retries of the same operation and use a different key for a different operation.") { OverriddenByPorts = ["idempotencyKey"] },
                new("retryDelayMs", "Retry Delay", "number", 250, 0, 5000, 10, Unit: "ms", Group: "Retry", Description: "Base delay between attempts; increases linearly with the attempt number."),


                new("diagnosticsLevel", "Diagnostics Level", "select", "standard", Group: "Diagnostics", Options: [new("Minimal", "minimal"), new("Standard", "standard"), new("Verbose", "verbose")], Description: "Controls how many fields are included in the diagnostics JSON payload."),
                new("retryPolicy", "Retry Policy", "select", "none", Options: [new("None", "none"), new("Once", "once"), new("Backoff", "backoff")], Internal: true, Description: "Optional soft retry when the first attempt hits a transient IO/timeout warning; does not mask hard validation failures.")],
            "Supports a dynamic url, raw body or value JSON input, and an idempotency key. GET/HEAD may retry according to retries; write requests retry only when explicitly enabled and an idempotency key is supplied, otherwise retries are suppressed and reported in diagnostics. The server must implement idempotent deduplication. Request and response sizes are capped, and a final non-2xx status fails the node.", "builtin",
            new VisionToolCapabilities(Deterministic: false, SupportsRunNode: false, SupportsParallel: false, ExecutionMode: VisionExecutionMode.WorkflowCore), Actions: [])
    ];
}
