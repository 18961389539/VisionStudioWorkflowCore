using VisionStudio.Abstractions;

namespace VisionStudio.Engine;

/// <summary>Catalog entries for the second image and measurement node batch.</summary>
public static class Batch2ImageCatalog
{
    private static readonly PortDescriptor Exec = new("exec", VisionDataType.Control);
    private static readonly PortDescriptor Next = new("next", VisionDataType.Control);
    private static readonly PortDescriptor Image = new("image", VisionDataType.Image);
    private static readonly PortDescriptor Roi = new("roi", VisionDataType.Roi, Required: false);
    private static readonly PortDescriptor OutImage = new("image", VisionDataType.Image);
    private static readonly PortDescriptor RoiMask = new("roiMask", VisionDataType.Image);
    private static readonly PortDescriptor CroppedRoi = new("croppedRoi", VisionDataType.Roi);
    private static readonly PortDescriptor CropOrigin = new("cropOrigin", VisionDataType.Point2D);
    private static readonly PortDescriptor CropTransform = new("transform", VisionDataType.Transform2D);
    private static readonly PortDescriptor[] Points = Enumerable.Range(1, 12).Select(i => new PortDescriptor($"point{i}", VisionDataType.Point2D, Required: false)).ToArray();

    public static IReadOnlyList<NodeCatalogItem> Items { get; } =
    [
        new("image.crop", "Crop Image", "Preprocess", [Exec, Image, Roi], [OutImage, RoiMask, CroppedRoi, CropOrigin, CropTransform, Next], [], "Crop the visible intersection of a rectangle, circle, polygon, or brush ROI to its tight bounding box. Parts outside the image are clipped; a selection covering no image pixels fails clearly. A connected roi input overrides the local ROI parameters, so the crop can follow a corrected ROI each run. Brush supports add and erase strokes; roiMask outputs the binary selection, while croppedRoi outputs the same selection translated to crop coordinates and can connect to downstream measurement nodes to prevent masked crop boundaries from being detected as edges. cropOrigin is the crop top-left in input-image coordinates, and transform maps CurrentImage geometry to CropImage coordinates. Its transform can compose after roi.transform as A: ReferenceImage→CurrentImage, B: CurrentImage→CropImage. Legacy x/y/width/height parameters remain supported.", Capabilities: new(SupportsRoi: true)),
        new("image.resize", "Resize Image", "Preprocess", [Exec, Image], [OutImage, Next], [new("width", "Width", "number", 640, 1, 16384, 1, Unit: "px"), new("height", "Height", "number", 480, 1, 16384, 1, Unit: "px"), new("interpolation", "Interpolation", "select", "linear", Options: [new("Nearest", "nearest"), new("Linear", "linear"), new("Cubic", "cubic"), new("Area", "area")])], "Resize an image using an explicit interpolation method."),
        new("image.rotate", "Rotate Image", "Preprocess", [Exec, Image], [OutImage, Next], [new("angleDeg", "Angle", "number", 0, -360, 360, 1, Unit: "deg"), new("expand", "Expand Canvas", "boolean", true)], "Rotate around the image center; optionally expand the canvas to retain all pixels."),
        new("image.perspective", "Perspective Correction", "Preprocess", [Exec, Image], [OutImage, Next], [new("x1", "Top Left X", "number", 0, 0, 16384, 1, Unit: "px"), new("y1", "Top Left Y", "number", 0, 0, 16384, 1, Unit: "px"), new("x2", "Top Right X", "number", 100, 0, 16384, 1, Unit: "px"), new("y2", "Top Right Y", "number", 0, 0, 16384, 1, Unit: "px"), new("x3", "Bottom Right X", "number", 100, 0, 16384, 1, Unit: "px"), new("y3", "Bottom Right Y", "number", 100, 0, 16384, 1, Unit: "px"), new("x4", "Bottom Left X", "number", 0, 0, 16384, 1, Unit: "px"), new("y4", "Bottom Left Y", "number", 100, 0, 16384, 1, Unit: "px"), new("width", "Output Width", "number", 100, 1, 16384, 1, Unit: "px"), new("height", "Output Height", "number", 100, 1, 16384, 1, Unit: "px")], "Rectify a four-corner quadrilateral into a rectangular image."),
        new("measure.pointLineDistance", "Point to Line Distance", "Measurement", [Exec, new("point", VisionDataType.Point2D), new("line", VisionDataType.Line2D)], [new("distance", VisionDataType.Double), new("closestPoint", VisionDataType.Point2D), Next], [new("segment", "Clamp to Segment", "boolean", false)], "Measure perpendicular distance to an infinite line or its finite segment."),
        new("measure.circleSpacing", "Circle Spacing", "Measurement", [Exec, new("circleA", VisionDataType.Circle), new("circleB", VisionDataType.Circle)], [new("centerDistance", VisionDataType.Double), new("gap", VisionDataType.Double), new("measurement", VisionDataType.Measurement), Next], [], "Return center distance and signed edge-to-edge gap; overlap produces a negative gap."),
        new("measure.fitLine", "Fit Line", "Measurement", [Exec, .. Points], [new("line", VisionDataType.Line2D), new("length", VisionDataType.Double), new("angle", VisionDataType.Double), new("rmsError", VisionDataType.Double), new("maxError", VisionDataType.Double), new("inlierCount", VisionDataType.Integer), Next], [], "Robust total-least-squares line fit (Tukey outlier rejection) from two or more connected Point2D inputs (up to 12). Outputs RMS/max perpendicular residuals and the inlier point count."),
        new("measure.fitCircle", "Fit Circle", "Measurement", [Exec, .. Points], [new("circle", VisionDataType.Circle), new("center", VisionDataType.Point2D), new("radius", VisionDataType.Double), new("rmsError", VisionDataType.Double), new("maxError", VisionDataType.Double), new("inlierCount", VisionDataType.Integer), Next], [], "Robust geometric circle fit (algebraic start, Gauss-Newton refinement, MAD outlier rejection) from at least three connected Point2D inputs (up to 12). Outputs RMS/max radial residuals and the inlier point count.")
    ];
}
