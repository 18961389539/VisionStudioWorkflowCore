namespace VisionStudio.Engine;

public static class ColorTextCatalog
{
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);
    private static readonly PortDescriptor Image = new("image", VisionDataType.Image);
    private static readonly PortDescriptor Region = new("region", VisionDataType.Region);
    private static readonly PortDescriptor[] ScopedImage = [Exec, new("image", VisionDataType.Image, Required: false), new("domainImage", VisionDataType.DomainImage, Required: false), new("region", VisionDataType.Region, Required: false)];
    private static readonly ParameterDescriptor Space = new("space", "Color Space", "select", "hsv", Options: [new("HSV (degrees, 0-1, 0-1)", "hsv"), new("sRGB (0-1)", "rgb"), new("CIELAB (D65)", "lab")],
        Description: "Target color space for conversion or range selection. HSV uses degrees for H; Lab uses D65.");
    public static IReadOnlyList<NodeCatalogItem> Items { get; } =
    [
        new("image.colorConvert", "Convert Color Space", "Preprocess", [Exec, Image],
            [new("channel1", VisionDataType.Image), new("channel2", VisionDataType.Image), new("channel3", VisionDataType.Image), new("channelNames", VisionDataType.String), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), Next], [Space,
                new("channelOrder", "Channel Order", "select", "bgr", Options: [new("BGR", "bgr"), new("RGB", "rgb")], Description: "Assumed channel order for multi-channel inputs.")],
            "Convert 8-bit sRGB/BGR(A) or replicated grayscale into three owned float32 channel images. RGB is 0..1; HSV is H=0..360 degrees and S/V=0..1; Lab uses D65 L*=0..100. Channels are scalar images, never packed pseudo-BGR.", "builtin"),
        new("measure.colorDifference", "Color Difference (Delta E 76)", "Measurement", [.. ScopedImage, new("reference", VisionDataType.Image, Required: false)],
            [new("found", VisionDataType.Boolean), new("area", VisionDataType.Double), Image, new("domainImage", VisionDataType.DomainImage), Region,
             new("deltaE", VisionDataType.Double, Required: false), new("maxDeltaE", VisionDataType.Double, Required: false), new("stdDev", VisionDataType.Double, Required: false), new("diagnostics", VisionDataType.String), Next],
            [new("referenceRed", "Reference Red (0-255)", "number", 255, 0, 255, 1, Description: "Constant reference sRGB red channel (integer 0..255) used when no reference image is connected."), new("referenceGreen", "Reference Green (0-255)", "number", 255, 0, 255, 1, Description: "Constant reference sRGB green channel (integer 0..255) used when no reference image is connected."), new("referenceBlue", "Reference Blue (0-255)", "number", 255, 0, 255, 1, Description: "Constant reference sRGB blue channel (integer 0..255) used when no reference image is connected.")],
            "Pixelwise CIE76 ΔE in sRGB/D65 Lab. Wire: aligned same-size reference image or a constant sRGB color; optional domain. deltaE is the mean of per-pixel distances, not the distance between mean colors. Empty regions omit scalars. For patch consistency, not semantic segmentation.", "builtin"),
        new("image.contrastNormalize", "Normalize Contrast", "Preprocess", [Exec, Image], [Image, new("inputMin", VisionDataType.Double), new("inputMax", VisionDataType.Double), new("meanIntensity", VisionDataType.Double), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("warningCount", VisionDataType.Integer), Next],
            [new("tailPercent", "Histogram Tail Percent", "number", 0, 0, 20, .1, Description: "Percentile tails to ignore when estimating input min/max (0 = use absolute min/max)."),
             new("outputMin", "Output Gray Minimum", "number", 0, 0, 255, 1, Description: "Lower gray of the stretched output (0..255). Must be <= outputMax."),
             new("outputMax", "Output Gray Maximum", "number", 255, 0, 255, 1, Description: "Upper gray of the stretched output (0..255)."),
                new("emptyPolicy", "Uniform Image Policy", "select", "passthrough", Options: [new("Passthrough", "passthrough"), new("Fail", "fail")], Description: "Behavior when the image has zero contrast.")],
            "Convert 8-bit input to gray and linearly stretch the native min/max or percentile bounds into the requested gray range, clipping outliers to that range. Uniform images pass through unchanged. Returns an owned 8-bit grayscale image.", "builtin"),
        new("image.illuminationNormalize", "Normalize Illumination", "Preprocess", [Exec, Image],
            [Image, new("meanIntensity", VisionDataType.Double), new("minIntensity", VisionDataType.Double), new("maxIntensity", VisionDataType.Double), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("warningCount", VisionDataType.Integer), Next],
            [new("windowSize", "Illumination Window", "number", 51, 3, 255, 2, Description: "Odd local mean window (3..255). Should exceed the feature size being preserved."),
             new("targetMean", "Target Gray", "number", 128, 0, 255, 1, Description: "Desired mean gray after division by the local background (0..255)."),
             new("minBackground", "Minimum Background Gray", "number", 1, .001, 255, .1, Description: "Floor for the estimated background to avoid division blow-ups.")],
            "Estimate a local mean background on 8-bit grayscale, clamp it by minBackground, then output image/background*targetMean as clipped 8-bit gray. The window should exceed the feature size. This is local illumination normalization, not a substitute for calibrated flat-field references.", "builtin"),
        new("image.sharpen", "Sharpen Image", "Preprocess", [Exec, Image],
            [Image, new("meanIntensity", VisionDataType.Double), new("minIntensity", VisionDataType.Double), new("maxIntensity", VisionDataType.Double), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("warningCount", VisionDataType.Integer), Next],
            [new("windowSize", "Sharpen Window", "number", 5, 3, 255, 2, Description: "Odd local-mean window for unsharp masking (3..255)."),
             new("amount", "Sharpen Amount", "number", 1, 0, 10, .1, Description: "Strength of image + amount*(image-localMean). 0 passes gray through; large values amplify noise.")],
            "JLVision mean-based unsharp masking on 8-bit grayscale: image + amount*(image-localMean), clipped to 0..255. May amplify noise; amount=0 passes gray pixels through. Color input is converted to gray.", "builtin"),
        new("text.validate", "Validate Text", "Recognition", [new("exec", VisionDataType.Control, Required: false), new("text", VisionDataType.String, Required: false), new("expected", VisionDataType.String, Required: false)],
            [new("pass", VisionDataType.Boolean), new("text", VisionDataType.String), new("expected", VisionDataType.String),
             new("mode", VisionDataType.String, Required: false), new("matchLength", VisionDataType.Integer, Required: false),
             new("matchedLength", VisionDataType.Integer, Required: false), new("matchIndex", VisionDataType.Integer, Required: false),
             new("matchCount", VisionDataType.Integer, Required: false), new("groupCount", VisionDataType.Integer, Required: false),
             new("matchedText", VisionDataType.String, Required: false), new("groupsJson", VisionDataType.String, Required: false), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("warningCount", VisionDataType.Integer), new("attemptCount", VisionDataType.Integer), Next],
            [new("text", "Text (fallback)", "text", "", Group: "Comparison", Description: "Fallback actual text used when the text input is disconnected.") { OverriddenByPorts = ["text"] },
             new("expected", "Expected Text", "text", "", Group: "Comparison", VisibleWhen: new("mode", ["equals", "contains", "startsWith", "endsWith"]), Description: "Expected text for equality/contains modes.") { OverriddenByPorts = ["expected"] },
             new("mode", "Validation Mode", "select", "equals", Group: "Comparison", Options:
             [new("Equals", "equals"), new("Contains", "contains"), new("Starts With", "startsWith"), new("Ends With", "endsWith"), new("Regular Expression", "regex")], Description: "Text comparison mode; regex mode uses the pattern, fullMatch, and timeout controls instead of expected text."),
             new("pattern", "Regular Expression", "text", "[A-Z0-9]+", Group: "Regex", VisibleWhen: new("mode", ["regex"]), Description: "Regular expression pattern when mode=regex."),
             new("fullMatch", "Match Entire Text", "boolean", true, Group: "Regex", VisibleWhen: new("mode", ["regex"]), Description: "Require the regex to match the entire string."),
             new("singleline", "Singleline Mode", "boolean", false, Group: "Regex", VisibleWhen: new("mode", ["regex"]), Description: "Enable RegexOptions.Singleline so '.' also matches newlines."),
             new("countMatches", "Count Every Match", "boolean", false, Group: "Regex", VisibleWhen: new("mode", ["regex"]),
                 Description: "Also scan for further matches so matchCount is exact. Costs extra regex work and can hit the timeout on backtracking patterns."),
             new("ignoreCase", "Ignore Case", "boolean", false, Group: "Comparison", Description: "Compare text case-insensitively when true."), new("trim", "Trim Outer Whitespace", "boolean", true, Group: "Comparison", Description: "Trim leading/trailing whitespace before compare."),
             new("allowEmpty", "Allow Empty Text", "boolean", false, Group: "Comparison", Description: "Allow empty actual text to pass when true."),
             new("timeoutMs", "Regex Timeout (ms)", "number", 100, 1, 1000, 1, Group: "Regex", VisibleWhen: new("mode", ["regex"]), Description: "Regex timeout in milliseconds; used only in Regular Expression mode."),
             new("minMatchedLength", "Minimum Matched Length (0=off)", "number", 0, 0, 100000, 1, Group: "Regex", VisibleWhen: new("mode", ["regex"]),
                 Description: "Pass only when the matched text is at least this many characters; 0 disables the gate."),
             new("emptyInputPolicy", "Empty Input Policy", "select", "allow", Group: "Regex", VisibleWhen: new("mode", ["regex"]), Options:
             [new("Allow (validate as-is)", "allow"), new("Fail the node", "fail"), new("No match", "nomatch")],
                 Description: "Regex mode only: allow keeps the allowEmpty semantics, fail stops the graph on empty text, nomatch returns pass=false."),
                ],
            "Compare text literally or validate it with a culture-invariant .NET regular expression. Connected values override their fallback parameters. Regex mode exposes matchedText and groupsJson; a bounded timeout prevents a slow pattern from blocking the workflow.",
            "builtin"),
    ];
}
