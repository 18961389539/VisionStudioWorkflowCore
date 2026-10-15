namespace VisionStudio.Engine;

public static class ColorTextCatalog
{
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);
    private static readonly PortDescriptor Image = new("image", VisionDataType.Image);
    private static readonly PortDescriptor Region = new("region", VisionDataType.Region);
    private static readonly PortDescriptor[] ScopedImage = [Exec, new("image", VisionDataType.Image, Required: false), new("domainImage", VisionDataType.DomainImage, Required: false), new("region", VisionDataType.Region, Required: false)];
    private static readonly ParameterDescriptor Space = new("space", "Color Space", "select", "hsv", Options: [new("HSV (degrees, 0-1, 0-1)", "hsv"), new("sRGB (0-1)", "rgb"), new("CIELAB (D65)", "lab")]);
    public static IReadOnlyList<NodeCatalogItem> Items { get; } =
    [
        new("image.colorConvert", "Convert Color Space", "Preprocess", [Exec, Image],
            [new("channel1", VisionDataType.Image), new("channel2", VisionDataType.Image), new("channel3", VisionDataType.Image), new("channelNames", VisionDataType.String), Next], [Space],
            "Convert 8-bit sRGB/BGR(A) or replicated grayscale into three owned float32 channel images. RGB is 0..1; HSV is H=0..360 degrees and S/V=0..1; Lab uses D65 L*=0..100. Channels are scalar images, never packed pseudo-BGR.", "builtin"),
        new("region.fromColorRange", "Color Range Region", "Region", ScopedImage,
            [Region, Image, new("count", VisionDataType.Integer), new("area", VisionDataType.Double), new("found", VisionDataType.Boolean), Next],
            [Space, new("min1", "Channel 1 Minimum", "number", 0, -360, 360, .01), new("max1", "Channel 1 Maximum", "number", 360, -360, 360, .01),
             new("min2", "Channel 2 Minimum", "number", 0, -200, 200, .01), new("max2", "Channel 2 Maximum", "number", 1, -200, 200, .01),
             new("min3", "Channel 3 Minimum", "number", .5, -200, 200, .01), new("max3", "Channel 3 Maximum", "number", 1, -200, 200, .01)],
            "Inclusive intersection of three color channel ranges within the image domain and optional region. HSV min1>max1 wraps through red (0 degrees); other ranges must be ordered. Hue is 0 for achromatic pixels; use a saturation minimum for hue inspection. Split connected components explicitly.", "builtin", new VisionToolCapabilities(EmitsOverlay: true)),
        new("measure.colorDifference", "Color Difference (Delta E 76)", "Measurement", [.. ScopedImage, new("reference", VisionDataType.Image, Required: false)],
            [new("found", VisionDataType.Boolean), new("area", VisionDataType.Double), Image, new("domainImage", VisionDataType.DomainImage), Region,
             new("deltaE", VisionDataType.Double, Required: false), new("maxDeltaE", VisionDataType.Double, Required: false), new("stdDev", VisionDataType.Double, Required: false), Next],
            [new("referenceRed", "Reference Red (0-255)", "number", 255, 0, 255, 1), new("referenceGreen", "Reference Green (0-255)", "number", 255, 0, 255, 1), new("referenceBlue", "Reference Blue (0-255)", "number", 255, 0, 255, 1)],
            "Pixelwise CIE76 Euclidean distance in sRGB/D65 Lab. Compare an aligned same-size reference image or a constant sRGB color. deltaE is the mean of pixel distances, not distance between mean colors. domainImage retains the effective inspection region for downstream thresholding; empty regions omit scalar differences.", "builtin"),
        new("image.contrastNormalize", "Normalize Contrast", "Preprocess", [Exec, Image], [Image, new("inputMin", VisionDataType.Double), new("inputMax", VisionDataType.Double), Next],
            [new("tailPercent", "Histogram Tail Percent", "number", 0, 0, 20, .1), new("outputMin", "Output Gray Minimum", "number", 0, 0, 255, 1), new("outputMax", "Output Gray Maximum", "number", 255, 0, 255, 1)],
            "Convert 8-bit input to gray and linearly stretch the native min/max or percentile bounds into the requested gray range, clipping outliers to that range. Uniform images pass through unchanged. Returns an owned 8-bit grayscale image.", "builtin"),
        new("image.illuminationNormalize", "Normalize Illumination", "Preprocess", [Exec, Image], [Image, Next],
            [new("windowSize", "Illumination Window", "number", 51, 3, 255, 2), new("targetMean", "Target Gray", "number", 128, 0, 255, 1), new("minBackground", "Minimum Background Gray", "number", 1, .001, 255, .1)],
            "Estimate a local mean background on 8-bit grayscale, clamp it by minBackground, then output image/background*targetMean as clipped 8-bit gray. The window should exceed the feature size. This is local illumination normalization, not a substitute for calibrated flat-field references.", "builtin"),
        new("image.sharpen", "Sharpen Image", "Preprocess", [Exec, Image], [Image, Next],
            [new("windowSize", "Sharpen Window", "number", 5, 3, 255, 2), new("amount", "Sharpen Amount", "number", 1, 0, 10, .1)],
            "JLVision mean-based unsharp masking on 8-bit grayscale: image + amount*(image-localMean), clipped to 0..255. May amplify noise; amount=0 passes gray pixels through. Color input is converted to gray.", "builtin"),
        new("text.validate", "Validate Text", "Recognition", [new("exec", VisionDataType.Control, Required: false), new("text", VisionDataType.String, Required: false), new("expected", VisionDataType.String, Required: false)],
            [new("pass", VisionDataType.Boolean), new("text", VisionDataType.String), new("expected", VisionDataType.String),
             new("matchedText", VisionDataType.String, Required: false), new("groupsJson", VisionDataType.String, Required: false), Next],
            [new("text", "Text (fallback)", "text", ""),
             new("expected", "Expected Text", "text", "", VisibleWhen: new("mode", ["equals", "contains", "startsWith", "endsWith"])),
             new("mode", "Validation Mode", "select", "equals", Options:
             [new("Equals", "equals"), new("Contains", "contains"), new("Starts With", "startsWith"), new("Ends With", "endsWith"), new("Regular Expression", "regex")]),
             new("pattern", "Regular Expression", "text", "[A-Z0-9]+", VisibleWhen: new("mode", ["regex"])),
             new("fullMatch", "Match Entire Text", "boolean", true, VisibleWhen: new("mode", ["regex"])),
             new("ignoreCase", "Ignore Case", "boolean", false), new("trim", "Trim Outer Whitespace", "boolean", true),
             new("allowEmpty", "Allow Empty Text", "boolean", false),
             new("timeoutMs", "Regex Timeout (ms)", "number", 100, 1, 1000, 1, VisibleWhen: new("mode", ["regex"]))],
            "Compare text literally or validate it with a culture-invariant .NET regular expression. Connected values override their fallback parameters. Regex mode exposes matchedText and groupsJson; a bounded timeout prevents a slow pattern from blocking the workflow.",
            "builtin"),
        new("text.match", "Match Text", "Recognition", [new("exec", VisionDataType.Control, Required: false), new("text", VisionDataType.String, Required: false), new("expected", VisionDataType.String, Required: false)],
            [new("pass", VisionDataType.Boolean), new("text", VisionDataType.String), new("expected", VisionDataType.String), Next],
            [new("text", "Text (fallback)", "text", ""), new("expected", "Expected Text", "text", ""), new("mode", "Text Match Mode", "select", "equals", Options: [new("Equals", "equals"), new("Contains", "contains"), new("Starts With", "startsWith"), new("Ends With", "endsWith")]),
             new("ignoreCase", "Ignore Case", "boolean", false), new("trim", "Trim Outer Whitespace", "boolean", true), new("allowEmpty", "Allow Empty Text", "boolean", false)],
            "Ordinal text comparison for OCR/barcode results. Connected text/expected override their fallback parameters. Optional outer whitespace trimming and culture-independent case folding; empty actual or expected fails unless allowEmpty=true. Maximum 65536 UTF-16 characters per input.", "builtin"),
        new("text.regex", "Validate Text with Regex", "Recognition", [Exec, new("text", VisionDataType.String)],
            [new("pass", VisionDataType.Boolean), new("matchedText", VisionDataType.String), new("groupsJson", VisionDataType.String), Next],
            [new("pattern", "Regular Expression", "text", "[A-Z0-9]+"), new("fullMatch", "Match Entire Text", "boolean", true), new("ignoreCase", "Ignore Case", "boolean", false),
             new("trim", "Trim Outer Whitespace", "boolean", true), new("allowEmpty", "Allow Empty Text", "boolean", false), new("timeoutMs", "Regex Timeout (ms)", "number", 100, 1, 1000, 1)],
            "Culture-invariant .NET regex validation with a bounded timeout. Full-match mode anchors the entire input; otherwise return the first match. Named/numbered groups are JSON strings. Invalid expressions and timeouts fail execution; no match returns pass=false. Limits: 65536 input characters, 4096 pattern characters, 128 groups and 1 MiB group JSON.", "builtin")
    ];
}
