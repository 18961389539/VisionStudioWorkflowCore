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
        new("region.fromColorRange", "Color Range Region", "Region", ScopedImage,
            [Region, Image, new("count", VisionDataType.Integer), new("area", VisionDataType.Double), new("found", VisionDataType.Boolean), new("diagnostics", VisionDataType.String, Required: false), Next],
            [Space, new("min1", "Channel 1 Minimum", "number", 0, -360, 360, .01, Group: "Color Range", Description: "Inclusive lower bound for channel 1 (H/L/R depending on space)."), new("max1", "Channel 1 Maximum", "number", 360, -360, 360, .01, Group: "Color Range", Description: "Inclusive upper bound for channel 1. HSV hue may wrap when min1>max1."),
             new("min2", "Channel 2 Minimum", "number", 0, -200, 200, .01, Group: "Color Range", Description: "Inclusive lower bound for channel 2 (S/a/G depending on space)."), new("max2", "Channel 2 Maximum", "number", 1, -200, 200, .01, Group: "Color Range", Description: "Inclusive upper bound for channel 2."),
             new("min3", "Channel 3 Minimum", "number", .5, -200, 200, .01, Group: "Color Range", Description: "Inclusive lower bound for channel 3 (V/b/B depending on space)."), new("max3", "Channel 3 Maximum", "number", 1, -200, 200, .01, Group: "Color Range", Description: "Inclusive upper bound for channel 3."),
                new("emptyPolicy", "Empty Result Policy", "select", "empty", Group: "Output", Options: [new("Emit empty", "empty"), new("Fail", "fail")], Description: "Behavior when no pixels pass the color range.")],
            "Inclusive intersection of three color-channel ranges within the image domain and optional region. In HSV, minH>maxH wraps through red; other ranges must be ordered. Hue is 0 for achromatic pixels — use a saturation floor for hue checks. Split connected components explicitly.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
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
             new("matchedText", VisionDataType.String, Required: false), new("groupsJson", VisionDataType.String, Required: false), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("warningCount", VisionDataType.Integer), new("attemptCount", VisionDataType.Integer), Next],
            [new("text", "Text (fallback)", "text", "", Group: "Comparison", Description: "Fallback actual text used when the text input is disconnected."),
             new("expected", "Expected Text", "text", "", Group: "Comparison", VisibleWhen: new("mode", ["equals", "contains", "startsWith", "endsWith"]), Description: "Expected text for equality/contains modes."),
             new("mode", "Validation Mode", "select", "equals", Group: "Comparison", Options:
             [new("Equals", "equals"), new("Contains", "contains"), new("Starts With", "startsWith"), new("Ends With", "endsWith"), new("Regular Expression", "regex")], Description: "Text comparison mode; regex mode uses the pattern, fullMatch, and timeout controls instead of expected text."),
             new("pattern", "Regular Expression", "text", "[A-Z0-9]+", Group: "Regex", VisibleWhen: new("mode", ["regex"]), Description: "Regular expression pattern when mode=regex."),
             new("fullMatch", "Match Entire Text", "boolean", true, Group: "Regex", VisibleWhen: new("mode", ["regex"]), Description: "Require the regex to match the entire string."),
             new("ignoreCase", "Ignore Case", "boolean", false, Group: "Comparison", Description: "Compare text case-insensitively when true."), new("trim", "Trim Outer Whitespace", "boolean", true, Group: "Comparison", Description: "Trim leading/trailing whitespace before compare."),
             new("allowEmpty", "Allow Empty Text", "boolean", false, Group: "Comparison", Description: "Allow empty actual text to pass when true."),
             new("timeoutMs", "Regex Timeout (ms)", "number", 100, 1, 1000, 1, Group: "Regex", VisibleWhen: new("mode", ["regex"]), Description: "Regex timeout in milliseconds; used only in Regular Expression mode."),






                ],
            "Compare text literally or validate it with a culture-invariant .NET regular expression. Connected values override their fallback parameters. Regex mode exposes matchedText and groupsJson; a bounded timeout prevents a slow pattern from blocking the workflow.",
            "builtin"),
        new("text.match", "Match Text", "Recognition", [new("exec", VisionDataType.Control, Required: false), new("text", VisionDataType.String, Required: false), new("expected", VisionDataType.String, Required: false)],
            [new("pass", VisionDataType.Boolean), new("text", VisionDataType.String), new("expected", VisionDataType.String), new("mode", VisionDataType.String), new("matchLength", VisionDataType.Integer), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("warningCount", VisionDataType.Integer), new("attemptCount", VisionDataType.Integer), Next],
            [new("text", "Text (fallback)", "text", "", Description: "Fallback actual text used when the text input is disconnected."), new("expected", "Expected Text", "text", "", Description: "Fallback expected text used when the expected input is disconnected."), new("mode", "Text Match Mode", "select", "equals", Options: [new("Equals", "equals"), new("Contains", "contains"), new("Starts With", "startsWith"), new("Ends With", "endsWith")], Description: "Comparison mode: exact, contains, startsWith, or endsWith."),
             new("ignoreCase", "Ignore Case", "boolean", false, Description: "Culture-invariant case-insensitive comparison."), new("trim", "Trim Outer Whitespace", "boolean", true, Description: "Strip leading/trailing whitespace from both sides before comparing."), new("allowEmpty", "Allow Empty Text", "boolean", false, Description: "When false, empty actual text fails without comparing."),









                ],
            "Ordinal text comparison for OCR/barcode results. Connected text/expected override their fallback parameters. Optional outer whitespace trimming and culture-independent case folding; empty actual or expected fails unless allowEmpty=true. Emits diagnostics. Maximum 65536 UTF-16 characters per input.", "builtin"),
        new("text.regex", "Validate Text with Regex", "Recognition", [Exec, new("text", VisionDataType.String)],
            [new("pass", VisionDataType.Boolean), new("matchedText", VisionDataType.String), new("matchedLength", VisionDataType.Integer), new("matchIndex", VisionDataType.Integer), new("matchCount", VisionDataType.Integer), new("groupsJson", VisionDataType.String), new("groupCount", VisionDataType.Integer), new("diagnostics", VisionDataType.String), new("overlay", VisionDataType.Image, Required: false), new("warningCount", VisionDataType.Integer), new("attemptCount", VisionDataType.Integer), Next],
            [new("pattern", "Regular Expression", "text", "[A-Z0-9]+", Group: "Pattern", Description: "Non-empty .NET regex. Named groups appear in groupsJson."),
             new("fullMatch", "Match Entire Text", "boolean", true, Group: "Match", Description: "When true, the pattern must match the entire (optionally trimmed) input."),
             new("ignoreCase", "Ignore Case", "boolean", false, Group: "Pattern", Description: "Culture-invariant case folding for the regex."),
             new("trim", "Trim Outer Whitespace", "boolean", true, Group: "Match", Description: "Strip leading/trailing whitespace before matching."),
             new("timeoutMs", "Regex Timeout (ms)", "number", 100, 1, 1000, 1, Group: "Match", Description: "Shared budget for searching and counting regex matches; cancellation and the elapsed budget are checked between matches, and each search also has a .NET regex timeout."),
             new("minMatchedLength", "Minimum Matched Length (0=off)", "number", 0, 0, 100000, 1, Group: "Match", Description: "Pass only when the matched text is at least this many characters; 0 disables the gate."),



                new("singleline", "Singleline Mode", "boolean", false, Group: "Pattern", Description: "Enable RegexOptions.Singleline (dot matches newline)."),
                new("emptyInputPolicy", "Empty Input Policy", "select", "fail", Group: "Match", Options: [new("Fail", "fail"), new("No match", "nomatch")], Description: "Behavior when the input text is empty."),




                ],
            "Culture-invariant .NET regex validation with a bounded timeout and diagnostics. Full-match mode anchors the entire input; otherwise return the first match. Named/numbered groups are JSON strings plus groupCount. Invalid expressions and timeouts fail execution; no match returns pass=false. Limits: 65536 input characters, 4096 pattern characters, 128 groups and 1 MiB group JSON.", "builtin")
    ];
}
