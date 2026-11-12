namespace VisionStudio.Engine;

public static partial class BuiltInNodeCatalog
{
    private static NodeCatalogItem[] ValueFlowItems =>
    [
        // ===== 批次一（2026-10-13 节点补齐）：通用数值 / 布尔判定族 =====
        new(
            "value.number",
            "Number Constant",
            "Value",
            [ExecIn],
            [new("value", VisionDataType.Double), new("integer", VisionDataType.Integer), new("isInteger", VisionDataType.Boolean), new("diagnostics", VisionDataType.String), new("isFinite", VisionDataType.Boolean), new("isZero", VisionDataType.Boolean), new("sign", VisionDataType.Integer), Next],
            [new("value", "Value", "number", 0, -1000000000, 1000000000, 1, Description: "Finite constant within the signed 64-bit integer range; non-finite values fail deterministically."),
                new("min", "Minimum (optional)", "number", 0, VisibleWhen: new("enforceBounds", ["true"]), Description: "Optional lower bound; when set with enforceBounds, values below fail."),
                new("max", "Maximum (optional)", "number", 0, VisibleWhen: new("enforceBounds", ["true"]), Description: "Optional upper bound; when set with enforceBounds, values above fail."),
                new("enforceBounds", "Enforce Min/Max", "boolean", false, Description: "When true, reject values outside min/max."),
                new("rounding", "Rounding Mode", "select", "away", Options: [new("Away from zero", "away"), new("To even", "even")], Description: "How the integer port is rounded from value."),
                ],
            "Emit a constant number (with an integer variant and an isInteger flag) for comparisons, arithmetic and thresholds. Non-finite or out-of-range values are rejected instead of silently propagating; includes JSON diagnostics.",
            "builtin"),
        new(
            "value.arithmetic",
            "Arithmetic",
            "Value",
            [ExecIn, new("a", VisionDataType.Double), new("b", VisionDataType.Double, Required: false) { RequiredWhen = new("op", ["add", "subtract", "multiply", "divide", "pow", "min", "max"]) }],
            [new("result", VisionDataType.Double), new("diagnostics", VisionDataType.String), new("isFinite", VisionDataType.Boolean), new("a", VisionDataType.Double), new("b", VisionDataType.Double), new("op", VisionDataType.String), Next],
            [
                new("op", "Operation", "select", "add", Description: "One of add/subtract/multiply/divide/pow/sqrt/abs/min/max/clamp; unknown operations fail instead of defaulting to add.", Options:
                [
                    new("Add (a + b)", "add"),
                    new("Subtract (a - b)", "subtract"),
                    new("Multiply (a * b)", "multiply"),
                    new("Divide (a / b)", "divide"),
                    new("Power (a ^ b)", "pow"),
                    new("Square root (a)", "sqrt"),
                    new("Absolute value (a)", "abs"),
                    new("Minimum (min a, b)", "min"),
                    new("Maximum (max a, b)", "max"),
                    new("Clamp (a)", "clamp")
                ]),
                new("clampMin", "Clamp Minimum", "number", 0, -1000000000, 1000000000, 0.001, Group: "Clamp", VisibleWhen: new("op", ["clamp"]), Description: "Lower bound used only by Clamp."),
                new("clampMax", "Clamp Maximum", "number", 1, -1000000000, 1000000000, 0.001, Group: "Clamp", VisibleWhen: new("op", ["clamp"]), Description: "Upper bound used only by Clamp. Must be greater than or equal to Clamp Minimum.")
            ,

                new("onNonFinite", "Non-Finite Policy", "select", "fail", Options: [new("Fail", "fail"), new("Clamp zero", "zero")], Description: "When the arithmetic result is non-finite: fail stops the graph; zero emits 0 with isFinite=false.")],
            "Combine measurements. Add, subtract, multiply, divide, power, min, and max read a and b. Square root, absolute value, and clamp read a only. An unknown operation, division by zero, a negative square root, reversed clamp bounds, or a non-finite result fails instead of guessing. Emits JSON diagnostics with operands, operation and result.",
            "builtin"),
        new(
            "value.range",
            "Range Check",
            "Value",
            [ExecIn, new("value", VisionDataType.Double)],
            [new("pass", VisionDataType.Boolean), new("inside", VisionDataType.Boolean), new("margin", VisionDataType.Double), new("deviation", VisionDataType.Double), new("clamped", VisionDataType.Double), new("diagnostics", VisionDataType.String), Next],
            [
                new("min", "Min", "number", 0, -1000000000, 1000000000, 1, Description: "Inclusive lower bound; reversed bounds are swapped and reported in diagnostics."),
                new("max", "Max", "number", 100, -1000000000, 1000000000, 1, Description: "Inclusive upper bound; must be finite."),
                new("invert", "Invert", "boolean", false, Description: "When true, pass means the value lies outside the inclusive window.")
            ,
                new("boundPolicy", "Bound Policy", "select", "inclusive", Options: [new("Inclusive", "inclusive"), new("Exclusive", "exclusive")], Description: "Whether min/max bounds are inclusive.")],
            "Inclusive range check. Reversed min/max are swapped and flagged in diagnostics. deviation is 0 inside the window and the signed distance outside it; clamped is the value limited to the window; margin is the signed distance to the nearest bound. invert flips pass only. Emits pass, inside, margin, deviation, clamped and JSON diagnostics.",
            "builtin"),
        new(
            "value.string",
            "String Tool",
            "Value",
            [ExecIn,
                new("a", VisionDataType.String, Required: false) { RequiredWhen = new("op", ["join", "tonumber", "substring", "trim", "replace", "pad", "upper", "lower", "length"]) },
                new("b", VisionDataType.String, Required: false),
                new("c", VisionDataType.String, Required: false),
                new("d", VisionDataType.String, Required: false),
                new("value", VisionDataType.Double, Required: false) { RequiredWhen = new("op", ["numbertostring"]) }],
            [new("textValue", VisionDataType.String), new("charCount", VisionDataType.Integer), new("numberValue", VisionDataType.Double, Required: false), new("truncated", VisionDataType.Boolean), new("diagnostics", VisionDataType.String), Next],
            [
                new("op", "Operation", "select", "template", Description: "Strictly one of the listed operations; unknown operations fail instead of passing text through.", Options:
                [
                    new("Template (named placeholders)", "template"),
                    new("Join with separator", "join"),
                    new("Number to text", "numbertostring"),
                    new("Text to number", "tonumber"),
                    new("Substring", "substring"),
                    new("Trim whitespace", "trim"),
                    new("Replace", "replace"),
                    new("Pad", "pad"),
                    new("Uppercase", "upper"),
                    new("Lowercase", "lower"),
                    new("Length", "length")
                ]),
                new("template", "String Template", "textarea", "{a}", Group: "Template", VisibleWhen: new("op", ["template"]),
                    Description: "Placeholders {a} {b} {c} {d} {value} and aliases {0}..{3} are replaced; {{ and }} emit literal braces. Unknown placeholders, unclosed braces and a {value} placeholder without a connected value input fail. Unconnected string inputs expand to an empty string."),
                new("separator", "Separator", "text", "_", Group: "Join", VisibleWhen: new("op", ["join"]),
                    Description: "Inserted between the non-empty parts of a, b, c and d. Empty parts are skipped; an empty separator concatenates without gaps."),
                new("format", "Number Format", "text", "0.###", Group: "Number", VisibleWhen: new("op", ["numbertostring"]),
                    Description: ".NET custom or standard numeric format string applied with invariant culture (0.### drops trailing zeros, F3 fixes three decimals, D6 zero-pads integers). Invalid formats fail."),
                new("startIndex", "Start Index", "number", 0, 0, 1000000, 1, Group: "Substring", VisibleWhen: new("op", ["substring"]),
                    Description: "Zero-based UTF-16 index into input a. Values past the end are clamped and reported as truncated=true."),
                new("takeLength", "Take Length (0=to end)", "number", 0, 0, 1000000, 1, Group: "Substring", VisibleWhen: new("op", ["substring"]),
                    Description: "Number of UTF-16 code units to take. 0 takes the remainder of the string; values past the end are clamped and reported as truncated=true."),
                new("trimSide", "Trim Side", "select", "both", Group: "Trim", VisibleWhen: new("op", ["trim"]), Options:
                [
                    new("Both ends", "both"),
                    new("Start only", "start"),
                    new("End only", "end")
                ], Description: "Which end of input a is stripped of Unicode whitespace."),
                new("find", "Find Text", "text", "", Group: "Replace", VisibleWhen: new("op", ["replace"]),
                    Description: "Literal text, or a regular expression when Use Regex is enabled. Must not be empty; at most 4096 characters."),
                new("replaceWith", "Replace With", "text", "", Group: "Replace", VisibleWhen: new("op", ["replace"]),
                    Description: "Written literally, with no $1 group expansion. May be empty to delete the matches."),
                new("replaceAll", "Replace All", "boolean", true, Group: "Replace", VisibleWhen: new("op", ["replace"]),
                    Description: "When false only the first match is replaced; when true every match is replaced."),
                new("useRegex", "Use Regex", "boolean", false, Group: "Replace", VisibleWhen: new("op", ["replace"]),
                    Description: "Treat Find Text as a .NET regular expression with a fixed 100 ms match timeout. Invalid patterns and timeouts fail the node."),
                new("ignoreCase", "Ignore Case", "boolean", false, Group: "Replace", VisibleWhen: new("op", ["replace"]),
                    Description: "Case-insensitive matching for both literal and regex modes."),
                new("padWidth", "Pad Width", "number", 8, 0, 1048576, 1, Group: "Pad", VisibleWhen: new("op", ["pad"]),
                    Description: "Target total length in UTF-16 code units. Text that is already this long or longer is returned unchanged (never truncated)."),
                new("padChar", "Pad Character", "text", "0", Group: "Pad", VisibleWhen: new("op", ["pad"]),
                    Description: "Exactly one character used to fill the gap; longer or empty values fail."),
                new("padSide", "Pad Side", "select", "left", Group: "Pad", VisibleWhen: new("op", ["pad"]), Options:
                [
                    new("Left", "left"),
                    new("Right", "right")
                ], Description: "Side that receives the padding characters."),
                new("maxLength", "Max Characters", "number", 65536, 16, 1048576, 1, Group: "Limits",
                    Description: "Upper bound on the produced string in UTF-16 code units. Results above it fail instead of being stored truncated.")
                ],
            "Compose and reshape strings that other nodes already produce (barcode text, OCR text, timestamps, paths). Wire: optional string inputs a/b/c/d and optional number input value; textValue is the result, charCount its UTF-16 length, truncated reports clamped substrings, numberValue is emitted only by Text to number. Template mode assembles {a}..{d} and {value}; Join skips empty parts; Number to text uses an invariant-culture format; Text to number parses with NumberStyles.Float and fails on non-numeric or non-finite text; Substring clamps out-of-range indexes and sets truncated; Trim/Replace/Pad/Uppercase/Lowercase/Length operate on input a. Empty find text, multi-character pad characters, unknown placeholders and invalid number formats fail deterministically; missing string inputs are treated as empty.",
            "builtin"),
        // ===== 通用列表类型配套（2026-10-26 · 缺口分析 P1-8）=====
        new(
            "value.numberList",
            "Number List",
            "Value",
            [ExecIn,
                new("a", VisionDataType.Double, Required: false), new("b", VisionDataType.Double, Required: false),
                new("c", VisionDataType.Double, Required: false), new("d", VisionDataType.Double, Required: false),
                new("e", VisionDataType.Double, Required: false), new("f", VisionDataType.Double, Required: false),
                new("g", VisionDataType.Double, Required: false), new("h", VisionDataType.Double, Required: false)],
            [new("numberList", VisionDataType.NumberList), new("count", VisionDataType.Integer), new("diagnostics", VisionDataType.String), Next],
            [],
            "Collect up to eight scalar measurements into one NumberList so value.statistics can summarize them. Only connected inputs are collected, in port order a..h: an unconnected input contributes nothing instead of a zero, because a silent zero would distort mean, range and CP/CPK. Non-finite inputs fail; connecting nothing fails with an explicit message rather than producing an empty list.",
            "builtin"),
        new(
            "value.statistics",
            "Statistics",
            "Value",
            [ExecIn, new("numberList", VisionDataType.NumberList)],
            [new("count", VisionDataType.Integer), new("mean", VisionDataType.Double), new("std", VisionDataType.Double),
                new("min", VisionDataType.Double), new("max", VisionDataType.Double), new("median", VisionDataType.Double),
                new("range", VisionDataType.Double), new("sum", VisionDataType.Double),
                new("cp", VisionDataType.Double, Required: false), new("cpk", VisionDataType.Double, Required: false),
                new("pass", VisionDataType.Boolean, Required: false), new("diagnostics", VisionDataType.String), Next],
            [
                new("deviation", "Deviation", "select", "sample", Group: "Statistics", Options:
                [
                    new("Sample (n-1)", "sample"),
                    new("Population (n)", "population")
                ], Description: "Standard deviation divisor. Sample is the default for measured samples; population treats the list as the whole population."),
                new("enableLimits", "Enable Spec Limits", "boolean", false, Group: "Capability", Description: "When false no cp/cpk/pass outputs are emitted, so downstream logic cannot mistake 'no specification' for 'in tolerance'."),
                new("lsl", "LSL", "number", 0, -1000000000, 1000000000, 0.001, Group: "Capability", VisibleWhen: new("enableLimits", ["true"]), Description: "Lower specification limit; must be strictly below USL."),
                new("usl", "USL", "number", 1, -1000000000, 1000000000, 0.001, Group: "Capability", VisibleWhen: new("enableLimits", ["true"]), Description: "Upper specification limit; must be strictly above LSL.")
                ],
            "Summarize a NumberList: count, mean, standard deviation, min, max, median, range and sum. Median averages the two middle samples for even counts. With spec limits enabled it also emits cp = (USL-LSL)/(6σ), cpk = min(USL-mean, mean-LSL)/(3σ) and pass = mean within [LSL, USL], using the selected deviation divisor; a zero standard deviation with limits enabled fails instead of returning infinity. Empty lists and non-finite samples fail.",
            "builtin"),
        new(
            "logic.boolean",
            "Boolean Logic",
            "Logic",
            [ExecIn, new("a", VisionDataType.Boolean), new("b", VisionDataType.Boolean, Required: false) { RequiredWhen = new("op", ["and", "or", "xor"]) }],
            [new("result", VisionDataType.Boolean), new("diagnostics", VisionDataType.String), new("operationApplied", VisionDataType.String), Next],
            [
                new("op", "Operation", "select", "and", Description: "Strictly one of and/or/xor/not; NOT uses input a only, unknown operations fail.", Options:
                [
                    new("AND", "and"),
                    new("OR", "or"),
                    new("XOR", "xor"),
                    new("NOT (a)", "not")
                ])
            ,

                new("invertResult", "Invert Result", "boolean", false, Description: "When true, negate the boolean result after applying op."),

                ],
            "Combine boolean results with strict operation validation (NOT uses a only; unknown operations fail instead of defaulting to AND). Feeds flow.result / flow.if for the final OK/NG disposition; emits JSON diagnostics.",
            "builtin"),
        new(
            "flow.if",
            "If",
            "Flow",
            [ExecIn, new("value", VisionDataType.Double), new("previous", VisionDataType.Boolean, Required: false)],
            [new("true", VisionDataType.Control), new("false", VisionDataType.Control), new("condition", VisionDataType.Boolean), new("value", VisionDataType.Double), new("margin", VisionDataType.Double), new("diagnostics", VisionDataType.String)],
            [
                new(
                    "operator",
                    "Operator",
                    "select",
                    ">",
                    Group: "Condition",
                    Options:
                    [
                        new(">", ">"),
                        new(">=", ">="),
                        new("<", "<"),
                        new("<=", "<="),
                        new("==", "=="),
                        new("!=", "!=")
                    ], Description: "Used only in threshold mode.", VisibleWhen: new("mode", ["threshold"])),
                new("threshold", "Compare Value", "number", 15000, -99999999, 99999999, 1, Group: "Condition", Description: "Used only in threshold mode.", VisibleWhen: new("mode", ["threshold"])),
                new("mode", "Compare Mode", "select", "threshold", Group: "Condition", Options:
                [
                    new("Threshold", "threshold"),
                    new("Inside range", "range")
                ], Description: "Threshold compares one value. Inside range checks a closed window and ignores the operator."),
                new("tolerance", "Tolerance", "number", 1e-9, 0, 1000000, 1e-9, Group: "Condition", Description: "Used only for == and != in threshold mode. Differences must be strictly less than this band; 0 requires exact equality.", VisibleWhen: new("mode", ["threshold"])) { VisibleWhenAll = [new("operator", ["==", "!="])] },
                new("rangeMin", "Range Minimum", "number", 0, -99999999, 99999999, 1, Group: "Condition", Description: "Inclusive lower bound used only in range mode. Must not exceed Range Maximum.", VisibleWhen: new("mode", ["range"])),
                new("rangeMax", "Range Maximum", "number", 100, -99999999, 99999999, 1, Group: "Condition", Description: "Inclusive upper bound used only in range mode.", VisibleWhen: new("mode", ["range"])),
                new("hysteresis", "Hysteresis", "number", 0, 0, 1000000, 0.001, Group: "Hysteresis", Description: "Applied only when the previous condition input is connected. There is no memory across runs. 0 disables it.")
            ,

                ],
            "Compare a number and branch true or false. Threshold mode keeps the original operators, with a configurable equality tolerance. Inside-range mode checks a closed window. Hysteresis uses the optional previous condition and does not remember state across runs. Emits condition, echoed value, signed margin and JSON diagnostics.",
            "builtin"),
        new(
            "flow.result",
            "Result / Disposition",
            "Flow",
            [ExecIn, new("pass", VisionDataType.Boolean)],
            [new("pass", VisionDataType.Boolean), new("disposition", VisionDataType.String), new("diagnostics", VisionDataType.String), new("warningCount", VisionDataType.Integer), Next],
            [new("okLabel", "OK Label", "text", "OK", Description: "Disposition string when pass is true."),
                new("ngLabel", "NG Label", "text", "NG", Description: "Disposition string when pass is false."),


                ],
            "Set the automatic inspection disposition (OK/NG) for traceability with JSON diagnostics. Inside Parallel branches the value is scoped to its branch and merged at Join with any-NG semantics.",
            "builtin"),
        new(
            "flow.parallel",
            "Parallel",
            "Flow",
            [ExecIn],
            [new("branch1", VisionDataType.Control), new("branch2", VisionDataType.Control), new("branches", VisionDataType.Integer), new("nodeId", VisionDataType.String), new("marker", VisionDataType.String), new("diagnostics", VisionDataType.String), new("elapsedMs", VisionDataType.Double)],
            [
                ],
            "Start two Workflow Core parallel paths. Emits a JSON diagnostics marker; branch scheduling and any-NG disposition merging are handled by the structured runtime.",
            "builtin"),
        .. Batch2ImageCatalog.Items.Select(item => item with { PluginId = "builtin" }),
        .. Batch2UtilityCatalog.Items,
        .. Batch3GeometryCatalog.Items.Select(item => item with { PluginId = "builtin" }),
        .. Batch3RecognitionCatalog.Items,
        .. Batch4IterationCatalog.Items.Select(item => item with { PluginId = "builtin" }),
        .. Batch5PrecisionCatalog.Items.Select(item => item with { PluginId = "builtin" }),
        .. CaliperWidthCatalog.Items,
        .. MetrologyCatalog.Items,
        .. JlVisionOperatorCatalog.Items,
        .. JlVisionAdvancedCatalog.Items,
        .. ContourThicknessCatalog.Items,
        .. RegionCatalog.Items,
        .. DefectCatalog.Items,
        .. AlignmentRegionCatalog.Items,
        .. ColorTextCatalog.Items,
        .. OpenCvExtraCatalog.Items,
        .. TextureFilterCatalog.Items,
        .. P0CompletionCatalog.Items,
        .. P1CompletionCatalog.Items,
        .. CharucoCatalog.Items,
        .. CSharpScriptCatalog.Items,
        .. GeometryToleranceCatalog.Items,
        .. MeasurementGapCatalog.Items,
        .. PreprocessGapCatalog.Items,
        .. RegionGapCatalog.Items,
        .. FeatureGapCatalog.Items,
        .. FeatureRegionCatalog.Items,
        .. ImageGeometryCatalog.Items,
        .. MeasurementAlignCatalog.Items,
        .. FeatureDetectorCatalog.Items,
        .. MatchingGeometryCatalog.Items,
        .. DefectAnalysisCatalog.Items,
        new(
            "flow.join",
            "Join",
            "Flow",
            [new("branch1", VisionDataType.Control), new("branch2", VisionDataType.Control)],
            [Next, new("joined", VisionDataType.Boolean), new("nodeId", VisionDataType.String), new("marker", VisionDataType.String), new("diagnostics", VisionDataType.String), new("warningCount", VisionDataType.Integer)],
            [],
            "Structured branch join marker. Merges Parallel branch dispositions with any-NG semantics (NG wins) into the run disposition; emits a JSON diagnostics marker for tracing.",
            "builtin")
    ];
}
