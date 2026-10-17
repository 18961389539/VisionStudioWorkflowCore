using VisionStudio.Abstractions;

namespace VisionStudio.Engine;

/// <summary>Catalog entries for barcode/OCR and ONNX inference recognition nodes.</summary>
public static class Batch3RecognitionCatalog
{
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);
    private static readonly PortDescriptor Image = new("image", VisionDataType.Image);

    private static readonly ParameterDescriptor[] OnnxParameters =
    [
        new("modelPath", "ONNX Model File", "text", string.Empty),
        new("outputName", "Output Name", "text", string.Empty, Description: "Optional ONNX output/layer name; leave empty for the model's default output."),
        new("inputWidth", "Input Width", "number", 224, 1, 4096, 1, Unit: "px"),
        new("inputHeight", "Input Height", "number", 224, 1, 4096, 1, Unit: "px"),
        new("scale", "Normalization Scale", "number", 0.00392156862745, -100, 100, 0.0001),
        new("meanB", "Mean B", "number", 0, -255, 255, 1),
        new("meanG", "Mean G", "number", 0, -255, 255, 1),
        new("meanR", "Mean R", "number", 0, -255, 255, 1),
        new("swapRB", "Swap Red and Blue", "boolean", false),
        new("crop", "Center Crop After Resize", "boolean", false)
    ];

    public static IReadOnlyList<NodeCatalogItem> Items { get; } =
    [
        new("recognition.barcode", "Barcode / QR Decode", "Recognition", [Exec, Image],
            [new("found", VisionDataType.Boolean), new("count", VisionDataType.Integer), new("results", VisionDataType.String), new("text", VisionDataType.String, Required: false), new("format", VisionDataType.String, Required: false), Next],
            [new("mode", "Formats", "select", "all", Options: [new("All Supported", "all"), new("QR Code", "qr"), new("1D Barcodes", "1d"), new("2D Codes (DataMatrix/PDF417/Aztec)", "2d")]), new("tryHarder", "Try Harder", "boolean", true), new("multiple", "Decode Multiple", "boolean", true)],
            "Decode QR/DataMatrix/PDF417/Aztec and supported 1D barcodes from the input image using ZXing.Net. results is a JSON array with decoded text, format and points; text/format expose the first result even when multiple codes are found.", "builtin",
            new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: false)),
        new("recognition.ocr", "OCR", "Recognition", [Exec, Image],
            [new("text", VisionDataType.String), new("confidence", VisionDataType.Double), new("wordCount", VisionDataType.Integer), new("words", VisionDataType.String), new("chars", VisionDataType.String), new("result", VisionDataType.String), Next],
            [
                new("dataPath", "Tessdata Directory", "text", string.Empty, Description: "Directory containing language .traineddata files."),
                new("language", "Language(s)", "text", "eng", Description: "Tesseract language codes, e.g. eng or eng+chi_sim."),
                new("psm", "Page Segmentation Mode", "select", "auto", Options: [new("Automatic", "auto"), new("Auto with OSD", "autoOsd"), new("Single Block", "singleblock"), new("Single Line", "singleline"), new("Single Word", "singleword"), new("Single Character", "singlechar"), new("Sparse Text", "sparsetext")]),
                new("oem", "OCR Engine Mode", "select", "default", Options: [new("Default (LSTM)", "default"), new("LSTM Only", "lstm"), new("Legacy Only", "legacy"), new("Legacy + LSTM", "combined")]),
                new("whitelist", "Character Whitelist", "text", string.Empty, Description: "Optional allowed characters, e.g. 0123456789 for numeric fields. Control characters are rejected."),
                new("dpi", "Image DPI (0=auto)", "number", 0, 0, 1200, 1),
                new("upscale", "Upscale Factor", "number", 1, 0.5, 4, 0.25, Description: "Resize before OCR; small print often reads better at 2x. Word boxes are mapped back to source pixels."),
                new("binarize", "Binarize", "select", "none", Options: [new("None (grayscale)", "none"), new("Otsu", "otsu"), new("Adaptive Gaussian", "adaptive")]),
                new("invert", "Invert Image", "boolean", false, Description: "Enable for light text on a dark background."),
                new("minConfidence", "Minimum Word Confidence", "number", 0, 0, 1, 0.01, Description: "Words below this confidence are dropped; when > 0, text is rebuilt from the kept words.")
            ],
            "Run Tesseract 5 OCR (OCRMax-style controls) on the image or a rectangular ROI parameter. Outputs full text, mean confidence, per-word JSON and per-character JSON (chars, from Tesseract box output; empty when the model produces none), with boxes in source pixels and 0-1 confidences. Font training/teaching is not supported; the configured tessdata directory must include each requested language model.", "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: false)),
        new("inference.classification", "ONNX Classification", "Inference", [Exec, Image],
            [new("result", VisionDataType.String), new("label", VisionDataType.String), new("classIndex", VisionDataType.Integer), new("confidence", VisionDataType.Double), Next],
            [.. OnnxParameters, new("labels", "Class Labels", "text", string.Empty, Description: "Optional comma-separated labels in model class order."), new("applySoftmax", "Apply Softmax", "boolean", true), new("topK", "Top K", "number", 5, 1, 100, 1)],
            "Run an ONNX image classifier with NCHW float input [1,3,H,W]. Supported output layouts: [1,C] and [1,C,1,1]; output result is JSON.", "builtin",
            new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: false)),
        new("inference.detection", "ONNX Detection", "Inference", [Exec, Image],
            [new("detections", VisionDataType.DetectionList), new("count", VisionDataType.Integer), new("result", VisionDataType.String), Next],
            [.. OnnxParameters, new("layout", "Output Layout", "select", "xyxy", Options: [new("Rows [x1,y1,x2,y2,score,class]", "xyxy"), new("YOLO (cx,cy,w,h + class scores)", "yolo"), new("YOLOv5 (with objectness)", "yolov5")]), new("coordinates", "Coordinate Units", "select", "normalized", Options: [new("Normalized 0-1", "normalized"), new("Input Pixels", "pixels")]), new("letterbox", "Letterboxed Input", "boolean", false, Description: "Enable when the model input was resized preserving aspect with symmetric padding; coordinates are un-padded before mapping to the source."), new("confidenceThreshold", "Confidence Threshold", "number", 0.25, 0, 1, 0.01), new("nmsThreshold", "NMS IoU Threshold", "number", 0.45, 0, 1, 0.01), new("maxCount", "Max Detections", "number", 100, 1, 1000, 1)],
            "Run an ONNX detector. Layout xyxy expects rows [x1,y1,x2,y2,score,classId] as [N,6] or [1,N,6]; layout yolo expects YOLOv8-style cx,cy,w,h plus per-class scores as [1,C,N], [1,N,C] or [N,C]. Coordinates can be normalized or input-image pixels, optionally letterboxed. Class-aware NMS is applied.", "builtin",
            new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: false)),
        new("inference.segmentation", "ONNX Segmentation", "Inference", [Exec, Image],
            [new("mask", VisionDataType.Image), new("classes", VisionDataType.String), new("classCount", VisionDataType.Integer), new("dominantClass", VisionDataType.Integer, Required: false), new("dominantFraction", VisionDataType.Double, Required: false), Next],
            [.. OnnxParameters, new("binaryThreshold", "Binary Threshold", "number", 0.5, -1000000, 1000000, 0.01, Description: "Used only when the output has one channel; multi-class output uses per-pixel argmax.")],
            "Run an ONNX semantic segmentation model with strict output layout [1,C,H,W], 1<=C<=256; emit an 8-bit class-label mask resized to the source dimensions plus per-class statistics (classes JSON: pixel count, image fraction, mean winning score) and the dominant class.", "builtin",
            new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: false)),
        new("inference.anomaly", "ONNX Anomaly Score", "Inference", [Exec, Image],
            [new("score", VisionDataType.Double), new("isAnomaly", VisionDataType.Boolean), new("heatmap", VisionDataType.Image, Required: false), new("peakX", VisionDataType.Double, Required: false), new("peakY", VisionDataType.Double, Required: false), new("areaFraction", VisionDataType.Double, Required: false), new("suggestedThreshold", VisionDataType.Double, Required: false), new("result", VisionDataType.String), Next],
            [.. OnnxParameters, new("threshold", "Anomaly Threshold", "number", 0.5, -1000000, 1000000, 0.01)],
            "Run an ONNX anomaly model. A scalar output is compared directly with the threshold; a spatial anomaly map ([1,1,H,W] or [1,H,W]) also emits a normalized heatmap resized to the source, the peak score/location and the fraction of map pixels at or above threshold (isAnomaly follows the peak).", "builtin",
            new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: false))
    ];
}
