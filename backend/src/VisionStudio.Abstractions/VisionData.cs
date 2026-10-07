namespace VisionStudio.Abstractions;

/// <summary>
/// Graph-visible payload types. Native implementation objects remain in-process behind VisionValue.
/// </summary>
public enum VisionDataType
{
    Control,
    Any,
    Image,
    FrameSet,
    Double,
    Integer,
    Boolean,
    String,
    Point2D,
    Line2D,
    Pose2D,
    Rectangle2D,
    Circle,
    Contour,
    DetectionList,
    Transform2D,
    FrameTree2D,
    CoordinatePoint2D,
    CoordinateLine2D,
    CoordinatePose2D,
    RobotTarget2D,
    DeviceTagValue,
    Measurement
}

/// <summary>
/// Engine-neutral image handle. A plugin may reference its own native image package and inspect NativeImage
/// without taking a compile-time dependency on VisionStudio.Engine.
/// </summary>
public interface IVisionImage
{
    int Width { get; }
    int Height { get; }
    object NativeImage { get; }
}


public sealed record VisionFrameSetFrameInfo(
    string CameraId,
    long Sequence,
    DateTimeOffset HostTimestamp,
    long? DeviceTimestampNs,
    long? TriggerId,
    string PixelFormat);

/// <summary>
/// Graph-visible synchronized multi-camera frame set. Implementations own the contained image resources.
/// </summary>
public interface IVisionFrameSet
{
    string GroupId { get; }
    string TimestampBasis { get; }
    double TriggerSkewUs { get; }
    bool WithinTolerance { get; }
    IReadOnlyDictionary<string, IVisionImage> Images { get; }
    IReadOnlyDictionary<string, VisionFrameSetFrameInfo> Frames { get; }
}

public readonly record struct VisionPoint2D(double X, double Y);
public readonly record struct VisionLine2D(VisionPoint2D Start, VisionPoint2D End)
{
    public double Length => Math.Sqrt(Math.Pow(End.X - Start.X, 2) + Math.Pow(End.Y - Start.Y, 2));
    public double AngleDeg => Math.Atan2(End.Y - Start.Y, End.X - Start.X) * 180.0 / Math.PI;
}
public readonly record struct VisionPose2D(double X, double Y, double ThetaDeg);

public sealed record VisionTransform2D(
    string SourceFrame,
    string TargetFrame,
    string TargetUnit,
    double H00, double H01, double H02,
    double H10, double H11, double H12,
    double H20, double H21, double H22,
    string SourceUnit = "px")
{
    public VisionPoint2D Apply(VisionPoint2D point)
    {
        var denominator = H20 * point.X + H21 * point.Y + H22;
        if (Math.Abs(denominator) < 1e-12)
            throw new InvalidOperationException("Transform2D produced a point at infinity.");
        return new VisionPoint2D(
            (H00 * point.X + H01 * point.Y + H02) / denominator,
            (H10 * point.X + H11 * point.Y + H12) / denominator);
    }

    public double ApplyAngleDeg(VisionPoint2D origin, double angleDeg)
    {
        var radians = angleDeg * Math.PI / 180.0;
        var p0 = Apply(origin);
        var p1 = Apply(new VisionPoint2D(origin.X + Math.Cos(radians), origin.Y + Math.Sin(radians)));
        return NormalizeAngleDeg(Math.Atan2(p1.Y - p0.Y, p1.X - p0.X) * 180.0 / Math.PI);
    }

    public double[] Matrix =>
    [
        H00, H01, H02,
        H10, H11, H12,
        H20, H21, H22
    ];

    public static double NormalizeAngleDeg(double angle)
    {
        while (angle > 180) angle -= 360;
        while (angle <= -180) angle += 360;
        return angle;
    }
}

public readonly record struct VisionCoordinatePoint2D(double X, double Y, string Frame, string Unit);
public readonly record struct VisionCoordinateLine2D(VisionCoordinatePoint2D Start, VisionCoordinatePoint2D End)
{
    public double Length => Math.Sqrt(Math.Pow(End.X - Start.X, 2) + Math.Pow(End.Y - Start.Y, 2));
}
public readonly record struct VisionCoordinatePose2D(double X, double Y, double ThetaDeg, string Frame, string Unit);
public readonly record struct VisionRobotTarget2D(
    double X,
    double Y,
    double RDeg,
    string Frame,
    string Unit,
    string Robot,
    string GuidanceMode);
public readonly record struct VisionDeviceTagValue(
    string DeviceId,
    string TagId,
    string DataType,
    object? Value,
    string Quality,
    DateTimeOffset Timestamp);
public readonly record struct VisionMeasurement(double Value, string Unit, string Kind);

public readonly record struct VisionRectangle2D(double X, double Y, double Width, double Height, double AngleDeg = 0);
public readonly record struct VisionCircle(double X, double Y, double Radius);

/// <summary>Runtime payload tagged with its graph-visible type.</summary>
public sealed record VisionValue(VisionDataType Type, object? Value)
{
    public static VisionValue Image(IVisionImage value) => new(VisionDataType.Image, value);
    public static VisionValue FrameSet(IVisionFrameSet value) => new(VisionDataType.FrameSet, value);
    public static VisionValue Double(double value) => new(VisionDataType.Double, value);
    public static VisionValue Integer(long value) => new(VisionDataType.Integer, value);
    public static VisionValue Boolean(bool value) => new(VisionDataType.Boolean, value);
    public static VisionValue String(string value) => new(VisionDataType.String, value);
    public static VisionValue Point(VisionPoint2D value) => new(VisionDataType.Point2D, value);
    public static VisionValue Line(VisionLine2D value) => new(VisionDataType.Line2D, value);
    public static VisionValue Pose(VisionPose2D value) => new(VisionDataType.Pose2D, value);
    public static VisionValue Rectangle(VisionRectangle2D value) => new(VisionDataType.Rectangle2D, value);
    public static VisionValue Circle(VisionCircle value) => new(VisionDataType.Circle, value);
    public static VisionValue Transform(VisionTransform2D value) => new(VisionDataType.Transform2D, value);
    public static VisionValue FrameTree(VisionFrameTree2D value) => new(VisionDataType.FrameTree2D, value);
    public static VisionValue CoordinatePoint(VisionCoordinatePoint2D value) => new(VisionDataType.CoordinatePoint2D, value);
    public static VisionValue CoordinateLine(VisionCoordinateLine2D value) => new(VisionDataType.CoordinateLine2D, value);
    public static VisionValue CoordinatePose(VisionCoordinatePose2D value) => new(VisionDataType.CoordinatePose2D, value);
    public static VisionValue RobotTarget(VisionRobotTarget2D value) => new(VisionDataType.RobotTarget2D, value);
    public static VisionValue DeviceTag(VisionDeviceTagValue value) => new(VisionDataType.DeviceTagValue, value);
    public static VisionValue Measurement(VisionMeasurement value) => new(VisionDataType.Measurement, value);

    public T Require<T>(string port)
    {
        if (Value is T typed) return typed;
        throw new InvalidOperationException($"Value on '{port}' is {Type}/{Value?.GetType().Name ?? "null"}, expected {typeof(T).Name}.");
    }
}
