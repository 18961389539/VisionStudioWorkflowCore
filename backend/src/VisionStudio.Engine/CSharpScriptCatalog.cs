using VisionStudio.Abstractions;

namespace VisionStudio.Engine;

/// <summary>Catalog contract for the built-in C# script node (2026-10-27).</summary>
internal static class CSharpScriptCatalog
{
    internal const string DefaultSource =
        """
        var a = Input.Number("a");
        Output.Number = a * 2;
        """;

    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);
    private static readonly PortDescriptor Image = new("image", VisionDataType.Image);

    internal static readonly IReadOnlyList<NodeCatalogItem> Items =
    [
        new("script.csharp", "C# Script", "Script",
            [
                Exec,
                new("image", VisionDataType.Image, Required: false),
                new("a", VisionDataType.Any, Required: false), new("b", VisionDataType.Any, Required: false),
                new("c", VisionDataType.Any, Required: false), new("d", VisionDataType.Any, Required: false),
                new("e", VisionDataType.Any, Required: false), new("f", VisionDataType.Any, Required: false),
                new("g", VisionDataType.Any, Required: false), new("h", VisionDataType.Any, Required: false)
            ],
            [
                new("result", VisionDataType.Any, Required: false),
                new("image", VisionDataType.Image, Required: false),
                new("number", VisionDataType.Double, Required: false),
                new("text", VisionDataType.String, Required: false),
                new("boolean", VisionDataType.Boolean, Required: false),
                new("json", VisionDataType.String, Required: false),
                new("diagnostics", VisionDataType.String),
                Next
            ],
            [
                new("source", "Script Source", "textarea", DefaultSource, Group: "Script",
                    Description: "C# script body. Use Input.Number/Integer/Boolean/Text/Numbers/Points, Image (Mat), Output.Result/Number/Text/Boolean/Json/Image, Log and Fail. Top-level return also becomes result when Output.Result is not set. Use a using block or explicit Dispose for working Mats; top-level 'using var' declarations are not accepted by the Roslyn script parser in this build."),
                new("timeoutMs", "Timeout (ms)", "number", 250, 10, 10_000, 10, Unit: "ms", Group: "Limits",
                    Description: "Wall-clock budget checked at injected loop/function ticks. Native OpenCV calls and BCL calls that do not yield cannot be interrupted."),
                new("maxTicks", "Maximum Ticks", "number", 1_000_000, 100, 100_000_000, 1_000, Group: "Limits",
                    Description: "Maximum loop/function ticks before the script is stopped. The rewriter inserts a tick at every loop body and function entry."),
                new("maxOutputBytes", "Maximum Output Bytes", "number", 65_536, 1, 1_048_576, 1, Unit: "bytes", Group: "Limits",
                    Description: "Maximum UTF-8 size of Output.Text/Output.Json; structured data must use valid JSON."),
                new("maxImagePixels", "Maximum Image Pixels", "number", 16_777_216, 1, 100_000_000, 1, Unit: "px", Group: "Limits",
                    Description: "Maximum input/output image pixel count. Reject oversized Mat objects before they can exhaust the host.")
            ],
            "Run trusted C# code with typed inputs and an optional OpenCV Mat image. Image input is cloned; image output is owned by the result. The runtime references only a minimal BCL surface plus OpenCvSharp and VisionStudio.Scripting; file/network/reflection/process/threading APIs and #r/#load directives are rejected by a syntax-tree allowlist. Loops and function entries receive a cooperative tick budget. Set Output.Result/Number/Text/Boolean/Json/Image or return a primitive value; at least one output is required. This is a trusted-engineer runtime, not a hard OS sandbox; imported workflows containing scripts require approval. algorithmBackend=roslyn.",
            "builtin", new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: false, Deterministic: false, ExecutionMode: VisionExecutionMode.WorkflowCore),
            Actions: [], Frames: [new(NodeImageFramePolicy.NewSpatialFrame, InputPort: "image", FrameLabel: "script output frame")])
    ];
}
