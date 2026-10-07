using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine;

/// <summary>
/// Canonical IPC codec for graph-visible values. V0.57 sends supported large images through a file-backed
/// memory mapped transport and falls back to lossless PNG for small/unsupported Mat types. Scalar/geometry
/// values use typed JSON. Unsupported native-only graph types fail closed.
/// </summary>
public static class PluginWorkerValueCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static PluginWorkerValue Encode(VisionValue value) => Encode(value, null);

    public static PluginWorkerValue Encode(VisionValue value, PluginWorkerSharedMemoryStore? sharedMemory)
    {
        if (value.Type == VisionDataType.Image)
        {
            var image = value.Require<IVisionImage>("worker-ipc");
            if (image.NativeImage is not Mat mat)
                throw new NotSupportedException($"Worker IPC supports OpenCvSharp Mat images; actual native image is {image.NativeImage.GetType().FullName}.");
            if (sharedMemory is not null && sharedMemory.CanUse(mat))
                return sharedMemory.EncodeImage(mat);
            Cv2.ImEncode(".png", mat, out var bytes);
            return new PluginWorkerValue(VisionDataType.Image, ImagePng: bytes);
        }

        if (value.Type == VisionDataType.Control)
            return new PluginWorkerValue(VisionDataType.Control, JsonSerializer.SerializeToElement(true, Json));
        if (value.Value is null)
            return new PluginWorkerValue(value.Type, JsonSerializer.SerializeToElement<object?>(null, Json));

        return value.Type switch
        {
            VisionDataType.Double => JsonValue(value.Type, Convert.ToDouble(value.Value)),
            VisionDataType.Integer => JsonValue(value.Type, Convert.ToInt64(value.Value)),
            VisionDataType.Boolean => JsonValue(value.Type, Convert.ToBoolean(value.Value)),
            VisionDataType.String => JsonValue(value.Type, Convert.ToString(value.Value) ?? ""),
            VisionDataType.Point2D => JsonValue(value.Type, (VisionPoint2D)value.Value),
            VisionDataType.Line2D => JsonValue(value.Type, (VisionLine2D)value.Value),
            VisionDataType.Pose2D => JsonValue(value.Type, (VisionPose2D)value.Value),
            VisionDataType.Rectangle2D => JsonValue(value.Type, (VisionRectangle2D)value.Value),
            VisionDataType.Circle => JsonValue(value.Type, (VisionCircle)value.Value),
            VisionDataType.Transform2D => JsonValue(value.Type, (VisionTransform2D)value.Value),
            VisionDataType.CoordinatePoint2D => JsonValue(value.Type, (VisionCoordinatePoint2D)value.Value),
            VisionDataType.CoordinateLine2D => JsonValue(value.Type, (VisionCoordinateLine2D)value.Value),
            VisionDataType.CoordinatePose2D => JsonValue(value.Type, (VisionCoordinatePose2D)value.Value),
            VisionDataType.RobotTarget2D => JsonValue(value.Type, (VisionRobotTarget2D)value.Value),
            VisionDataType.DeviceTagValue => JsonValue(value.Type, (VisionDeviceTagValue)value.Value),
            VisionDataType.Measurement => JsonValue(value.Type, (VisionMeasurement)value.Value),
            _ => throw new NotSupportedException($"VisionDataType.{value.Type} is not supported by Plugin Worker protocol v{VisionPluginWorkerProtocol.Version}. Keep this tool InProcess or add an explicit wire codec.")
        };
    }

    public static VisionValue Decode(PluginWorkerValue wire) => Decode(wire, null);

    public static VisionValue Decode(PluginWorkerValue wire, PluginWorkerSharedMemoryStore? sharedMemory)
    {
        if (wire.Type == VisionDataType.Image)
        {
            if (wire.SharedImage is not null)
            {
                if (sharedMemory is null)
                    throw new InvalidOperationException("Worker image payload uses shared memory but no shared-memory store was configured.");
                return sharedMemory.DecodeImage(wire.SharedImage, deleteAfterRead: true);
            }
            if (wire.ImagePng is not { Length: > 0 }) throw new InvalidOperationException("Worker image payload is empty.");
            var mat = Cv2.ImDecode(wire.ImagePng, ImreadModes.Unchanged);
            if (mat.Empty()) { mat.Dispose(); throw new InvalidOperationException("Worker image PNG could not be decoded."); }
            return VisionValue.Image(VisionImage.Own(mat));
        }

        if (wire.Type == VisionDataType.Control)
            return new VisionValue(VisionDataType.Control, true);
        var json = wire.Json ?? throw new InvalidOperationException($"Worker payload for {wire.Type} has no JSON value.");
        return wire.Type switch
        {
            VisionDataType.Double => VisionValue.Double(json.GetDouble()),
            VisionDataType.Integer => VisionValue.Integer(json.GetInt64()),
            VisionDataType.Boolean => VisionValue.Boolean(json.GetBoolean()),
            VisionDataType.String => VisionValue.String(json.GetString() ?? ""),
            VisionDataType.Point2D => VisionValue.Point(json.Deserialize<VisionPoint2D>(Json)),
            VisionDataType.Line2D => VisionValue.Line(json.Deserialize<VisionLine2D>(Json)),
            VisionDataType.Pose2D => VisionValue.Pose(json.Deserialize<VisionPose2D>(Json)),
            VisionDataType.Rectangle2D => VisionValue.Rectangle(json.Deserialize<VisionRectangle2D>(Json)),
            VisionDataType.Circle => VisionValue.Circle(json.Deserialize<VisionCircle>(Json)),
            VisionDataType.Transform2D => VisionValue.Transform(json.Deserialize<VisionTransform2D>(Json) ?? throw new InvalidOperationException("Invalid Transform2D payload.")),
            VisionDataType.CoordinatePoint2D => VisionValue.CoordinatePoint(json.Deserialize<VisionCoordinatePoint2D>(Json)),
            VisionDataType.CoordinateLine2D => VisionValue.CoordinateLine(json.Deserialize<VisionCoordinateLine2D>(Json)),
            VisionDataType.CoordinatePose2D => VisionValue.CoordinatePose(json.Deserialize<VisionCoordinatePose2D>(Json)),
            VisionDataType.RobotTarget2D => VisionValue.RobotTarget(json.Deserialize<VisionRobotTarget2D>(Json)),
            VisionDataType.DeviceTagValue => VisionValue.DeviceTag(json.Deserialize<VisionDeviceTagValue>(Json)),
            VisionDataType.Measurement => VisionValue.Measurement(json.Deserialize<VisionMeasurement>(Json)),
            _ => throw new NotSupportedException($"VisionDataType.{wire.Type} is not supported by Plugin Worker protocol v{VisionPluginWorkerProtocol.Version}.")
        };
    }

    public static IReadOnlyDictionary<string, JsonElement> EncodeSummary(IReadOnlyDictionary<string, object?> summary) =>
        summary.ToDictionary(
            pair => pair.Key,
            pair => JsonSerializer.SerializeToElement(pair.Value, pair.Value?.GetType() ?? typeof(object), Json),
            StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, object?> DecodeSummary(IReadOnlyDictionary<string, JsonElement> summary) =>
        summary.ToDictionary(pair => pair.Key, pair => (object?)pair.Value.Clone(), StringComparer.OrdinalIgnoreCase);

    private static PluginWorkerValue JsonValue<T>(VisionDataType type, T value) =>
        new(type, JsonSerializer.SerializeToElement(value, Json));
}
