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
        new("swapRB", "Swap Red and Blue", "boolean", false, Description: "Enable for RGB-trained models such as Ultralytics YOLO (OpenCV loads BGR)."),
        new("crop", "Center Crop After Resize", "boolean", false)
    ];


    /// <summary>ONNX preprocess defaults tuned for Ultralytics YOLO exports (imgsz=640).</summary>
    private static readonly ParameterDescriptor[] OnnxYoloParameters =
    [
        new("modelPath", "ONNX Model File", "text", string.Empty),
        new("outputName", "Output Name", "text", string.Empty, Description: "Optional ONNX output/layer name; leave empty for the model's default output."),
        new("inputWidth", "Input Width", "number", 640, 1, 4096, 1, Unit: "px"),
        new("inputHeight", "Input Height", "number", 640, 1, 4096, 1, Unit: "px"),
        new("scale", "Normalization Scale", "number", 0.00392156862745, -100, 100, 0.0001),
        new("meanB", "Mean B", "number", 0, -255, 255, 1),
        new("meanG", "Mean G", "number", 0, -255, 255, 1),
        new("meanR", "Mean R", "number", 0, -255, 255, 1),
        new("swapRB", "Swap Red and Blue", "boolean", true, Description: "Default on for RGB-trained Ultralytics YOLO (OpenCV loads BGR)."),
        new("crop", "Center Crop After Resize", "boolean", false)
    ];

    public static IReadOnlyList<NodeCatalogItem> Items { get; } =
    [
        new("recognition.barcode", "Barcode / QR Decode", "Recognition", [Exec, Image],
            [new("found", VisionDataType.Boolean), new("count", VisionDataType.Integer), new("results", VisionDataType.String), new("text", VisionDataType.String, Required: false), new("format", VisionDataType.String, Required: false), Next],
            [
                new("mode", "Formats", "select", "all", Options: [new("All Supported", "all"), new("QR Code", "qr"), new("1D Barcodes", "1d"), new("2D Codes (DataMatrix/PDF417/Aztec)", "2d")]),
                new("tryHarder", "Try Harder", "boolean", true),
                new("multiple", "Decode Multiple", "boolean", true),
                new("preprocess", "Preprocessing", "select", "auto", Options: [new("Automatic fallback", "auto"), new("Original only", "none"), new("Otsu threshold", "otsu"), new("Adaptive threshold", "adaptive")], Description: "Automatic tries original grayscale, Otsu, and adaptive threshold images."),
                new("tryInverted", "Try Inverted Polarity", "boolean", true, Description: "Retries with inverted pixels when no normal-polarity code is found; multi-code mode also searches inverted pixels."),
                new("adaptiveBlockSize", "Adaptive Window Size", "number", 31, 3, 101, 2, Description: "Odd local window size; reduced automatically for small images."),
                new("adaptiveConstant", "Adaptive Threshold Offset", "number", 10, -100, 100, 1),
                new("maxResults", "Maximum Results", "number", 100, 1, 1000, 1)
            ],
            "Decode QR/DataMatrix/PDF417/Aztec and supported 1D barcodes using ZXing.Net. Supports an image ROI, threshold fallback passes, optional inverted-polarity retries, and multiple symbols. Results include decoded text, format, source-image points, and the preprocessing pass; detected points are drawn on the image preview. text/format expose the first result.", "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: true)),
        new("recognition.ocr", "OCR", "Recognition", [Exec, Image],
            [new("text", VisionDataType.String), new("confidence", VisionDataType.Double), new("wordCount", VisionDataType.Integer), new("words", VisionDataType.String), new("chars", VisionDataType.String), new("result", VisionDataType.String), Next],
            [
                new("dataPath", "Tessdata Directory", "text", string.Empty, Description: "Directory containing language .traineddata files."),
                new("language", "Language(s)", "text", "eng", Description: "Tesseract language codes, e.g. eng or eng+chi_sim."),
                new("psm", "Page Segmentation Mode", "select", "auto", Options: [new("Automatic", "auto"), new("Auto with OSD", "autoOsd"), new("Single Block", "singleblock"), new("Single Line", "singleline"), new("Single Word", "singleword"), new("Single Character", "singlechar"), new("Sparse Text", "sparsetext")]),
                new("oem", "OCR Engine Mode", "select", "default", Options: [new("Default (LSTM)", "default"), new("LSTM Only", "lstm"), new("Legacy Only", "legacy"), new("Legacy + LSTM", "combined")]),
                new("whitelist", "Character Whitelist", "text", string.Empty, Description: "Optional allowed characters, e.g. 0123456789 for numeric fields. Control characters are rejected."),
                new("blacklist", "Character Blacklist", "text", string.Empty, Description: "Optional forbidden characters (tessedit_char_blacklist). A character cannot be in both whitelist and blacklist."),
                new("dpi", "Image DPI (0=auto)", "number", 0, 0, 1200, 1),
                new("upscale", "Upscale Factor", "number", 1, 0.5, 4, 0.25, Description: "Resize before OCR; small print often reads better at 2x. Word boxes are mapped back to source pixels."),
                new("binarize", "Binarize", "select", "none", Options: [new("None (grayscale)", "none"), new("Otsu", "otsu"), new("Adaptive Gaussian", "adaptive")]),
                new("invert", "Invert Image", "boolean", false, Description: "Enable for light text on a dark background."),
                new("minConfidence", "Minimum Word Confidence", "number", 0, 0, 1, 0.01, Description: "Words below this confidence are dropped; when > 0, text is rebuilt from the kept words.")
            ],
            "Run Tesseract 5 OCR (OCRMax-style controls) on the image or a rectangular ROI parameter. Outputs full text, mean confidence, per-word JSON and per-character JSON (chars, from Tesseract box output; empty when the model produces none), with boxes in source pixels and 0-1 confidences. Font training/teaching is not supported; the configured tessdata directory must include each requested language model.", "builtin",
            new VisionToolCapabilities(SupportsRoi: true, EmitsOverlay: false)),
        new("inference.classification", "ONNX Classification", "Inference", [Exec, Image],
            [new("result", VisionDataType.String), new("label", VisionDataType.String), new("classIndex", VisionDataType.Integer), new("confidence", VisionDataType.Double), new("accepted", VisionDataType.Boolean), new("margin", VisionDataType.Double), new("entropy", VisionDataType.Double), new("classCount", VisionDataType.Integer), new("diagnostics", VisionDataType.String), Next],
            [.. OnnxParameters,
                new("layout", "Output Layout", "select", "scores", Options: [new("Logits / scores (optional softmax)", "scores"), new("YOLO26 classify (already softmax)", "yolo26")], Description: "YOLO26-cls ONNX exports [1,C] probabilities with softmax already in the graph; choose yolo26 to skip a second softmax."),
                new("letterbox", "Letterbox Resize", "boolean", false, Description: "Preserve aspect with gray (114) padding. Usually off for classifiers."),
                new("labels", "Class Labels", "text", string.Empty, Description: "Optional comma-separated labels in model class order; the count must match the model class count or the field must stay empty."),
                new("applySoftmax", "Apply Softmax", "boolean", true, Description: "Ignored when layout=yolo26 (probabilities are already normalized)."),
                new("topK", "Top K", "number", 5, 1, 100, 1, Description: "Number of ranked classes in the result JSON (clamped to the class count); values outside 1–100 fail."),
                new("minimumConfidence", "Minimum Classification Confidence (0=off)", "number", 0, 0, 1, 0.01, Description: "Emit accepted=false when the winning probability is below this threshold. Requires softmax probabilities; 0 disables the gate.")],
            "Run an ONNX image classifier with NCHW float input [1,3,H,W]. Output [1,C] or [1,C,1,1]. layout=scores applies optional numerically stable softmax; layout=yolo26 matches Ultralytics YOLO26-cls ONNX (already softmaxed, probabilities are validated and never re-softmaxed). Label lists must match the class count or be empty; unknown layouts and invalid topK fail deterministically. Emits top-K JSON plus best label/index/confidence, an optional minimum-confidence acceptance signal, winner margin, distribution entropy, class count and JSON diagnostics. No training/evaluation is performed. Tip: set swapRB=true for RGB-trained YOLO models.", "builtin",
            new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: false)),
        new("inference.detection", "ONNX Detection", "Inference", [Exec, Image],
            [new("detections", VisionDataType.DetectionList), new("count", VisionDataType.Integer), new("result", VisionDataType.String), Next],
            [.. OnnxParameters, new("layout", "Output Layout", "select", "xyxy", Options: [new("Rows [x1,y1,x2,y2,score,class]", "xyxy"), new("YOLO (cx,cy,w,h + class scores)", "yolo"), new("YOLOv5 (with objectness)", "yolov5")]), new("coordinates", "Coordinate Units", "select", "normalized", Options: [new("Normalized 0-1", "normalized"), new("Input Pixels", "pixels")]), new("letterbox", "Letterboxed Input", "boolean", false, Description: "Enable for Ultralytics YOLO: preprocess uses gray (114) letterbox AND coordinates are un-padded before mapping to the source."), new("confidenceThreshold", "Confidence Threshold", "number", 0.25, 0, 1, 0.01), new("nmsThreshold", "NMS IoU Threshold", "number", 0.45, 0, 1, 0.01), new("maxCount", "Max Detections", "number", 100, 1, 1000, 1)],
            "Run an ONNX detector. Layout xyxy expects rows [x1,y1,x2,y2,score,classId] as [N,6] or [1,N,6] (YOLO26 end-to-end nms=False export is typically [1,300,6]); layout yolo expects YOLOv8/YOLO26 raw cx,cy,w,h plus per-class scores as [1,C,N]; layout yolov5 adds objectness. Enable letterbox so preprocess pads like Ultralytics and coordinates un-pad correctly. Class-aware NMS is applied here (not embedded).", "builtin",
            new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: false)),
        new("inference.obb", "ONNX Oriented Box Detection (YOLO-OBB)", "Inference", [Exec, Image],
            [new("detections", VisionDataType.DetectionList), new("count", VisionDataType.Integer), new("overlay", VisionDataType.Image), new("result", VisionDataType.String), new("diagnostics", VisionDataType.String), Next],
            [.. OnnxYoloParameters,
                new("layout", "Output Layout", "select", "yolo", Options: [new("Raw YOLOv8/YOLO26 OBB head", "yolo"), new("YOLO26 end-to-end xywhr rows", "xywhr")], Description: "Raw head: [1,5+nc,N] (cx,cy,w,h,class scores,angle rad). End-to-end: [1,N,7] rows (cx,cy,w,h,score,classId,angle rad)."),
                new("tensorOrder", "Raw Tensor Order", "select", "auto", Options: [new("Automatic (smaller dimension is channels)", "auto"), new("Channels First [1,C,N]", "channelsFirst"), new("Channels Last [1,N,C]", "channelsLast")], Description: "Only used for raw 3D tensors. Auto detects the smaller channel dimension; select an explicit order for unusual class counts or small input sizes."),
                new("coordinates", "Coordinate Units", "select", "pixels", Options: [new("Input Pixels", "pixels"), new("Normalized 0-1", "normalized")]),
                new("letterbox", "Letterboxed Input", "boolean", true, Description: "Default on: Ultralytics gray (114) letterbox with exact padding removal. Disable only when source/model aspect ratios match."),
                new("confidenceThreshold", "Confidence Threshold", "number", 0.25, 0, 1, 0.01),
                new("nmsThreshold", "Rotated IoU Threshold", "number", 0.45, 0, 1, 0.01),
                new("nmsMode", "NMS Mode", "select", "auto", Options: [new("Automatic (raw=on, end-to-end=off)", "auto"), new("On", "on"), new("Off", "off")], Description: "Uses exact oriented polygon IoU and class-aware suppression. Automatic mode skips NMS on YOLO26 end-to-end predictions."),
                new("maxCount", "Max Detections", "number", 100, 1, 1000, 1),
                new("labels", "Class Labels", "text", string.Empty, Description: "Optional comma-separated labels in model class order; leave empty to emit class_N labels.")],
            "Run YOLOv8/YOLO26 oriented object detection. Supports raw OBB heads and YOLO26 end-to-end xywhr rows; angles are radians. Raw candidates receive class-aware rotated IoU NMS, while automatic mode skips suppression for end-to-end output. Returns DetectionList with an oriented Rectangle2D, polygon/angle JSON, and a labeled overlay image. Letterbox is enabled by default; stretch resizing is rejected when its unequal axis scaling would distort rotated boxes.", "builtin",
            new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: true)),
        new("inference.segmentation", "ONNX Semantic Segmentation", "Inference", [Exec, Image],
            [new("mask", VisionDataType.Image), new("overlay", VisionDataType.Image), new("classes", VisionDataType.String), new("classCount", VisionDataType.Integer), new("dominantClass", VisionDataType.Integer, Required: false), new("dominantFraction", VisionDataType.Double, Required: false), new("meanConfidence", VisionDataType.Double), new("lowConfidenceFraction", VisionDataType.Double), Next],
            [.. OnnxParameters,
                new("layout", "Output Layout", "select", "logits", Options: [new("Logits [1,C,H,W] (argmax)", "logits"), new("YOLO26 semantic class map", "yolo26-semantic")], Description: "logits keeps legacy multi-channel scores; yolo26-semantic matches Ultralytics YOLO26-sem ONNX where argmax is already in the graph ([1,H,W])."),
                new("letterbox", "Letterbox Resize", "boolean", false, Description: "Preserve aspect with gray (114) padding. Enable when the YOLO26-sem export used letterbox."),
                new("binaryThreshold", "Binary Threshold", "number", 0.5, -1000000, 1000000, 0.01, Description: "Used only for layout=logits with one channel; multi-class logits use per-pixel argmax. Ignored for yolo26-semantic."),
                new("lowConfidenceThreshold", "Low Confidence Threshold", "number", 0.5, -1000000, 1000000, 0.01, Description: "Fraction of pixels whose winning score is below this value is reported as lowConfidenceFraction. For yolo26-semantic, scores are treated as 1.0.")],
            "Semantic segmentation only. layout=logits: [1,C,H,W] argmax mask. layout=yolo26-semantic: YOLO26-sem ONNX class map [1,H,W]/[H,W]/[1,1,H,W] (argmax already applied). Emits label mask, colour overlay, per-class stats. For YOLO instance masks (yolo26n-seg) use inference.instanceSegmentation.", "builtin",
            new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: false)),
        new("inference.instanceSegmentation", "ONNX Instance Segmentation (YOLO-Seg)", "Inference", [Exec, Image],
            [new("detections", VisionDataType.DetectionList), new("count", VisionDataType.Integer), new("mask", VisionDataType.Image), new("overlay", VisionDataType.Image), new("instances", VisionDataType.String), new("result", VisionDataType.String), Next],
            [.. OnnxYoloParameters,
                new("protoOutputName", "Prototype Output Name", "text", string.Empty, Description: "Second ONNX output (mask prototypes). Leave empty with outputName empty to use the model's first two outputs in order."),
                new("layout", "Output Layout", "select", "yolo26-seg", Options: [new("Raw YOLO-Seg head", "yolo26-seg"), new("YOLO26 end-to-end rows", "xyxy")], Description: "Raw head: [1,4+nc+maskDim,N]. YOLO26 end-to-end: [1,N,6+maskDim] rows [x1,y1,x2,y2,score,class,coeffs]. Both use the prototype tensor [1,maskDim,H,W]."),
                new("coordinates", "Coordinate Units", "select", "pixels", Options: [new("Input Pixels", "pixels"), new("Normalized 0-1", "normalized")]),
                new("letterbox", "Letterboxed Input", "boolean", true, Description: "Default on: Ultralytics exports assume letterbox preprocess; disable only for stretch-trained custom exports."),
                new("confidenceThreshold", "Confidence Threshold", "number", 0.25, 0, 1, 0.01),
                new("nmsThreshold", "NMS IoU Threshold", "number", 0.45, 0, 1, 0.01),
                new("nmsMode", "NMS Mode", "select", "auto", Options: [new("Automatic (raw=on, end-to-end=off)", "auto"), new("On", "on"), new("Off", "off")], Description: "YOLO26 end-to-end output is already NMS-free top detections; automatic mode skips duplicate suppression there and applies class-aware NMS to raw heads."),
                new("maskThreshold", "Mask Threshold", "number", 0.5, 0, 1, 0.01),
                new("includeMaskRuns", "Include Per-Instance Masks", "boolean", true, Description: "Include each binary mask in instances JSON as row-major alternating background/foreground run lengths. Turn off to reduce payload size; the combined class mask remains available."),
                new("maxCount", "Max Instances", "number", 100, 1, 1000, 1)],
            "Run Ultralytics YOLO26/YOLOv8 instance-seg ONNX with raw or YOLO26 end-to-end rows plus mask prototypes. Raw heads use class-aware NMS; end-to-end rows skip it in automatic mode. Composes each mask from sigmoid(coeff·proto), crops to the box, and maps through letterboxing. The class-id label mask is a highest-score-wins composite; instances JSON can preserve overlapping masks independently with row-major run-length encoding. For semantic masks use inference.segmentation; for rotated boxes use inference.obb; for keypoints use inference.pose.", "builtin",
            new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: true)),
        new("inference.pose", "ONNX Pose (YOLO-Pose)", "Inference", [Exec, Image],
            [new("detections", VisionDataType.DetectionList), new("count", VisionDataType.Integer), new("keypoints", VisionDataType.String), new("overlay", VisionDataType.Image), new("result", VisionDataType.String), Next],
            [.. OnnxYoloParameters,
                new("layout", "Output Layout", "select", "yolo", Options: [new("YOLO raw (YOLOv8/YOLO26 nms=None)", "yolo"), new("YOLO26 end-to-end xyxy+kpts", "xyxy")], Description: "yolo: [1,4+nc+K*D,N] e.g. COCO [1,56,8400]. xyxy: [1,N,6+K*D] e.g. YOLO26 nms=False [1,300,57]."),
                new("coordinates", "Coordinate Units", "select", "pixels", Options: [new("Input Pixels", "pixels"), new("Normalized 0-1", "normalized")]),
                new("letterbox", "Letterboxed Input", "boolean", true, Description: "Default on: Ultralytics letterbox preprocess and coordinate un-padding."),
                new("numKeypoints", "Keypoint Count", "number", 17, 1, 128, 1, Description: "COCO pose uses 17. Must match the exported model."),
                new("keypointDims", "Keypoint Dims", "number", 3, 2, 3, 1, Description: "3 = x,y,conf (Ultralytics default); 2 = x,y only."),
                new("confidenceThreshold", "Confidence Threshold", "number", 0.25, 0, 1, 0.01),
                new("nmsThreshold", "NMS IoU Threshold", "number", 0.45, 0, 1, 0.01),
                new("keypointThreshold", "Keypoint Draw Threshold", "number", 0.5, 0, 1, 0.01, Description: "Keypoints / skeleton bones below this confidence are skipped on the overlay."),
                new("drawSkeleton", "Draw COCO Skeleton", "boolean", true, Description: "When numKeypoints=17, draw the standard COCO-17 skeleton on the overlay."),
                new("maxCount", "Max Poses", "number", 100, 1, 1000, 1)],
            "Run Ultralytics YOLO26/YOLOv8 pose ONNX. layout=yolo decodes the raw multi-candidate head (external class-aware NMS). layout=xyxy matches YOLO26 end-to-end NMS-free rows [x1,y1,x2,y2,score,class,kpts...]. Outputs person boxes, keypoints JSON (x,y,conf in source pixels), and an overlay. Set swapRB=true (default here) and letterbox=true for Ultralytics exports. Custom keypoint topologies work if numKeypoints/keypointDims match; skeleton drawing is COCO-17 only.", "builtin",
            new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: true)),
        new("inference.anomaly", "ONNX Anomaly Score", "Inference", [Exec, Image],
            [new("score", VisionDataType.Double), new("isAnomaly", VisionDataType.Boolean), new("heatmap", VisionDataType.Image, Required: false), new("peakX", VisionDataType.Double, Required: false), new("peakY", VisionDataType.Double, Required: false), new("areaFraction", VisionDataType.Double, Required: false), new("suggestedThreshold", VisionDataType.Double, Required: false), new("result", VisionDataType.String), Next],
            [.. OnnxParameters, new("threshold", "Anomaly Threshold", "number", 0.5, -1000000, 1000000, 0.01), new("thresholdMode", "Threshold Mode", "select", "manual", Options: [new("Manual Threshold", "manual"), new("Otsu Auto (spatial maps)", "otsu")], Description: "Otsu derives the effective threshold from the spatial anomaly map distribution (suggestedThreshold); scalar outputs always use the manual threshold.")],
            "Run an ONNX anomaly model. A scalar output is compared directly with the manual threshold; a spatial anomaly map ([1,1,H,W] or [1,H,W]) also emits a normalized heatmap resized to the source, the peak score/location and the fraction of map pixels at or above the effective threshold (isAnomaly follows the peak). thresholdMode=otsu uses the Otsu threshold of the map itself as the effective threshold.", "builtin",
            new VisionToolCapabilities(SupportsRoi: false, EmitsOverlay: false))
    ];
}
