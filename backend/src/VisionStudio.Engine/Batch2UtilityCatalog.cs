using VisionStudio.Abstractions;

namespace VisionStudio.Engine;

/// <summary>Directory entries for batch-two utility/flow/output executors. Host registration remains explicit.</summary>
public static class Batch2UtilityCatalog
{
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);

    public static IReadOnlyList<NodeCatalogItem> Items { get; } =
    [
        new("flow.delay", "Delay", "Flow", [Exec], [new("elapsedMs", VisionDataType.Double), Next],
            [new("delayMs", "Delay", "number", 1000, 0, 600000, 10, Unit: "ms")],
            "Wait for the configured duration; cancellation stops the wait immediately.", "builtin",
            new VisionToolCapabilities(SupportsParallel: false, ExecutionMode: VisionExecutionMode.WorkflowCore), Actions: []),
        new("flow.ifBoolean", "Boolean If", "Flow", [Exec, new("condition", VisionDataType.Boolean)],
            [new("true", VisionDataType.Control), new("false", VisionDataType.Control), new("condition", VisionDataType.Boolean)], [],
            "Choose the true or false path from the Boolean condition input, then converge the paths at a Join.", "builtin",
            new VisionToolCapabilities(ExecutionMode: VisionExecutionMode.WorkflowCore), Actions: []),
        new("flow.merge", "Flow Merge", "Flow",
            Enumerable.Range(1, 16).Select(index => new PortDescriptor($"case{index}", VisionDataType.Control, Required: false))
                .Append(new PortDescriptor("default", VisionDataType.Control, Required: false)).ToArray(),
            [Next], [], "Converge selected If/Switch branches. A real incoming branch is required by structured workflow compilation.", "builtin",
            new VisionToolCapabilities(ExecutionMode: VisionExecutionMode.WorkflowCore), Actions: []),
        new("flow.try", "Try / Catch", "Flow", [Exec],
            [new("body", VisionDataType.Control), new("success", VisionDataType.Control), new("error", VisionDataType.Control), new("succeeded", VisionDataType.Boolean), new("errorMessage", VisionDataType.String), new("attempts", VisionDataType.Integer)],
            [new("maxRetries", "Max Retries", "number", 0, 0, 3, 1), new("retryDelayMs", "Retry Delay", "number", 100, 0, 5000, 10, Unit: "ms")],
            "Catch body failures and route to success or error. Automatic retries are limited to linear deterministic built-in Pipeline compute nodes with no declared actions; cancellation is never retried. Connect body/success/error to flow.merge case1/case2/default.", "builtin",
            new VisionToolCapabilities(Deterministic: false, SupportsRunNode: false, SupportsParallel: false, ExecutionMode: VisionExecutionMode.WorkflowCore), Actions: []),
        new("flow.switch", "Switch", "Flow", [Exec, new("value", VisionDataType.Any)],
            [new("selected", VisionDataType.String), new("case1", VisionDataType.Control), new("case2", VisionDataType.Control), new("case3", VisionDataType.Control), new("case4", VisionDataType.Control), new("case5", VisionDataType.Control), new("case6", VisionDataType.Control), new("case7", VisionDataType.Control), new("case8", VisionDataType.Control), new("case9", VisionDataType.Control), new("case10", VisionDataType.Control), new("case11", VisionDataType.Control), new("case12", VisionDataType.Control), new("case13", VisionDataType.Control), new("case14", VisionDataType.Control), new("case15", VisionDataType.Control), new("case16", VisionDataType.Control), new("default", VisionDataType.Control)],
            [new("casesJson", "Cases (JSON string array)", "textarea", "[\"OK\",\"NG\"]")],
            "Choose one matching path. casesJson entries map in order to case1-case16; unmatched values take the default path. Converge all configured paths at Flow Merge.", "builtin",
            new VisionToolCapabilities(ExecutionMode: VisionExecutionMode.WorkflowCore), Actions: []),
        new("output.saveImage", "Save Image", "Output", [Exec, new("image", VisionDataType.Image)],
            [new("path", VisionDataType.String), new("bytesWritten", VisionDataType.Integer), Next],
            [new("path", "Image Path", "text", "artifacts/output.png"), new("overwrite", "Overwrite Existing", "boolean", false)],
            "Encode and persist an image using the extension-selected OpenCV codec. Paths are resolved by the host process.", "builtin",
            new VisionToolCapabilities(Deterministic: false, SupportsRunNode: false, SupportsParallel: false, ExecutionMode: VisionExecutionMode.WorkflowCore), Actions: []),
        new("output.exportResult", "Export Result", "Output", [Exec, new("value", VisionDataType.Any)],
            [new("path", VisionDataType.String), new("bytesWritten", VisionDataType.Integer), Next],
            [new("path", "Result Path", "text", "artifacts/result.json"), new("format", "Format", "select", "json", Options: [new("JSON", "json"), new("CSV", "csv")]), new("overwrite", "Overwrite Existing", "boolean", false)],
            "Write one typed result value to JSON or a quoted CSV row.", "builtin",
            new VisionToolCapabilities(Deterministic: false, SupportsRunNode: false, SupportsParallel: false, ExecutionMode: VisionExecutionMode.WorkflowCore), Actions: []),
        new("output.httpRequest", "HTTP Request", "Output", [Exec],
            [new("statusCode", VisionDataType.Integer), new("body", VisionDataType.String), new("success", VisionDataType.Boolean), Next],
            [new("url", "URL", "text", "https://example.invalid/"), new("method", "Method", "select", "GET", Options: [new("GET", "GET"), new("POST", "POST"), new("PUT", "PUT"), new("DELETE", "DELETE"), new("HEAD", "HEAD")]), new("body", "Request Body", "textarea", ""), new("contentType", "Content Type", "text", "application/json"), new("timeoutMs", "Timeout", "number", 10000, 1, 120000, 100, Unit: "ms"), new("maxResponseBytes", "Response Size Limit", "number", 1048576, 1, 67108864, 1024, Unit: "bytes")],
            "Send an HTTP request. Cancellation/deadline abort the request; any non-2xx response fails the node.", "builtin",
            new VisionToolCapabilities(Deterministic: false, SupportsRunNode: false, SupportsParallel: false, ExecutionMode: VisionExecutionMode.WorkflowCore), Actions: [])
    ];
}
