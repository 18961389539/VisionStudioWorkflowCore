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
            [new("text", VisionDataType.String), new("confidence", VisionDataType.Double), new("result", VisionDataType.String), Next],
            [new("dataPath", "Tessdata Directory", "text", string.Empty, Description: "Directory containing language .traineddata files."), new("language", "Language(s)", "text", "eng", Description: "Tesseract language codes, e.g. eng or eng+chi_sim.")],
            "Run Tesseract 5 OCR on the image. The configured tessdata directory must include each requested language model.", "builtin",
            new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: false)),
        new("inference.classification", "ONNX Classification", "Inference", [Exec, Image],
            [new("result", VisionDataType.String), new("label", VisionDataType.String), new("classIndex", VisionDataType.Integer), new("confidence", VisionDataType.Double), Next],
            [.. OnnxParameters, new("labels", "Class Labels", "text", string.Empty, Description: "Optional comma-separated labels in model class order."), new("applySoftmax", "Apply Softmax", "boolean", true), new("topK", "Top K", "number", 5, 1, 100, 1)],
            "Run an ONNX image classifier with NCHW float input [1,3,H,W]. Supported output layouts: [1,C] and [1,C,1,1]; output result is JSON.", "builtin",
            new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: false)),
        new("inference.detection", "ONNX Detection", "Inference", [Exec, Image],
            [new("detections", VisionDataType.DetectionList), new("count", VisionDataType.Integer), new("result", VisionDataType.String), Next],
            [.. OnnxParameters, new("coordinates", "Coordinate Units", "select", "normalized", Options: [new("Normalized 0-1", "normalized"), new("Input Pixels", "pixels")]), new("confidenceThreshold", "Confidence Threshold", "number", 0.25, 0, 1, 0.01), new("nmsThreshold", "NMS IoU Threshold", "number", 0.45, 0, 1, 0.01), new("maxCount", "Max Detections", "number", 100, 1, 1000, 1)],
            "Run an ONNX detector with output rows [x1,y1,x2,y2,score,classId], as [N,6] or [1,N,6]. Coordinates can be normalized or input-image pixels.", "builtin",
            new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: false)),
        new("inference.segmentation", "ONNX Segmentation", "Inference", [Exec, Image],
            [new("mask", VisionDataType.Image), Next],
            [.. OnnxParameters, new("binaryThreshold", "Binary Threshold", "number", 0.5, -1000000, 1000000, 0.01, Description: "Used only when the output has one channel; multi-class output uses per-pixel argmax.")],
            "Run an ONNX semantic segmentation model with strict output layout [1,C,H,W], 1<=C<=256; emit an 8-bit class-label mask resized to the source dimensions.", "builtin",
            new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: false)),
        new("inference.anomaly", "ONNX Anomaly Score", "Inference", [Exec, Image],
            [new("score", VisionDataType.Double), new("isAnomaly", VisionDataType.Boolean), new("result", VisionDataType.String), Next],
            [.. OnnxParameters, new("threshold", "Anomaly Threshold", "number", 0.5, -1000000, 1000000, 0.01)],
            "Run an ONNX anomaly model with a single scalar float32 output tensor; compare its score with the configured threshold.", "builtin",
            new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: false))
    ];
}
