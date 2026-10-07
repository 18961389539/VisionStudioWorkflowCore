using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

public sealed class AcquireImageNode(CameraManager cameraManager) : IVisionNodeExecutor
{
    public string Type => "image.acquire";

    public async ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context,
        NodeDefinition node,
        CancellationToken cancellationToken)
    {
        var replayInputPath = node.GetString("__replayInputPath", string.Empty);
        if (!string.IsNullOrWhiteSpace(replayInputPath))
        {
            var full = Path.GetFullPath(replayInputPath);
            if (!File.Exists(full)) throw new FileNotFoundException("Offline replay input image was not found.", full);
            var mat = OpenCvSharp.Cv2.ImRead(full, OpenCvSharp.ImreadModes.Unchanged);
            if (mat.Empty())
            {
                mat.Dispose();
                throw new InvalidDataException("Offline replay input image could not be decoded.");
            }
            var replayImage = VisionImage.Own(mat);
            var replaySequenceText = node.GetString("__replaySequence", "0");
            var replaySequence = long.TryParse(replaySequenceText, out var parsedReplaySequence) ? parsedReplaySequence : 0L;
            var replayTimestamp = node.GetString("__replayTimestamp", DateTimeOffset.UtcNow.ToString("O"));
            var replayCameraId = node.GetString("__replayCameraId", "offline-replay");
            return new NodeExecutorResult(
                new Dictionary<string, VisionValue>
                {
                    ["image"] = VisionValue.Image(replayImage),
                    ["sequence"] = VisionValue.Integer(replaySequence),
                    ["timestamp"] = VisionValue.String(replayTimestamp),
                    ["cameraId"] = VisionValue.String(replayCameraId)
                },
                new Dictionary<string, object?>
                {
                    ["camera"] = replayCameraId,
                    ["source"] = Path.GetFileName(full),
                    ["size"] = $"{replayImage.Width}x{replayImage.Height}",
                    ["replay"] = true
                });
        }

        var cameraId = node.GetString("cameraId", "virtual-1");
        var timeoutMs = node.GetInt("timeoutMs", 1500);
        var autoStart = node.GetBool("autoStart", node.GetBool("autoOpen", true));
        var triggerBeforeGrab = node.GetBool("triggerBeforeGrab", false);
        var frameModeText = node.GetString("frameMode", "Latest");
        var frameMode = Enum.TryParse<CameraFrameMode>(frameModeText, true, out var parsed)
            ? parsed
            : CameraFrameMode.Latest;

        var lease = await cameraManager.AcquireAsync(
            cameraId,
            frameMode,
            TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 50, 10000)),
            autoStart,
            triggerBeforeGrab,
            cancellationToken);

        var image = VisionImage.FromLease(lease);
        return new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(image),
                ["sequence"] = VisionValue.Integer(lease.Sequence),
                ["timestamp"] = VisionValue.String(lease.Timestamp.ToString("O")),
                ["cameraId"] = VisionValue.String(lease.CameraId)
            },
            new Dictionary<string, object?>
            {
                ["camera"] = lease.CameraId,
                ["sequence"] = lease.Sequence,
                ["timestamp"] = lease.Timestamp.ToString("O"),
                ["pixelFormat"] = lease.PixelFormat,
                ["size"] = $"{image.Width}x{image.Height}",
                ["frameMode"] = frameMode.ToString(),
                ["triggerBeforeGrab"] = triggerBeforeGrab
            });
    }
}

public sealed class SynchronizedCaptureNode(ISynchronizedFrameSetService synchronization) : IVisionNodeExecutor
{
    public string Type => "camera.syncCapture";

    public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var groupId = node.GetString("groupId", "sync-group-1");
        var scheduled = node.GetBool("scheduled", false);
        var leadTimeMs = node.GetInt("leadTimeMs", 100);
        var timeoutMs = node.GetInt("frameTimeoutMs", 3000);
        var frameSet = await synchronization.CaptureAsync(new SynchronizedFrameSetCaptureRequest(
            groupId, scheduled, leadTimeMs, Math.Clamp(timeoutMs, 100, 30000)), cancellationToken);

        return new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["frameSet"] = VisionValue.FrameSet(frameSet),
                ["skewUs"] = VisionValue.Double(frameSet.TriggerSkewUs),
                ["timestampBasis"] = VisionValue.String(frameSet.TimestampBasis),
                ["withinTolerance"] = VisionValue.Boolean(frameSet.WithinTolerance),
                ["cameraCount"] = VisionValue.Integer(frameSet.Images.Count)
            },
            new Dictionary<string, object?>
            {
                ["groupId"] = frameSet.GroupId,
                ["cameraCount"] = frameSet.Images.Count,
                ["triggerSkewUs"] = frameSet.TriggerSkewUs,
                ["timestampBasis"] = frameSet.TimestampBasis,
                ["withinTolerance"] = frameSet.WithinTolerance,
                ["cameras"] = frameSet.Frames.Values.Select(x => new { x.CameraId, x.Sequence, x.DeviceTimestampNs, x.TriggerId }).ToArray()
            });
    }
}

public sealed class FrameSetImageNode : IVisionNodeExecutor
{
    public string Type => "frameset.image";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var frameSet = context.Require<IVisionFrameSet>("frameSet");
        var cameraId = node.GetString("cameraId", string.Empty);
        if (string.IsNullOrWhiteSpace(cameraId))
            throw new InvalidOperationException("FrameSet Image requires cameraId.");
        if (!frameSet.Images.TryGetValue(cameraId, out var image))
            throw new KeyNotFoundException($"FrameSet '{frameSet.GroupId}' does not contain camera '{cameraId}'. Available: {string.Join(", ", frameSet.Images.Keys)}");
        var frame = frameSet.Frames[cameraId];
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(image),
                ["sequence"] = VisionValue.Integer(frame.Sequence),
                ["timestamp"] = VisionValue.String(frame.HostTimestamp.ToString("O")),
                ["deviceTimestampNs"] = frame.DeviceTimestampNs is { } ns ? VisionValue.Integer(ns) : new VisionValue(VisionDataType.Integer, null),
                ["triggerId"] = frame.TriggerId is { } triggerId ? VisionValue.Integer(triggerId) : new VisionValue(VisionDataType.Integer, null),
                ["cameraId"] = VisionValue.String(cameraId)
            },
            new Dictionary<string, object?>
            {
                ["groupId"] = frameSet.GroupId,
                ["cameraId"] = cameraId,
                ["sequence"] = frame.Sequence,
                ["deviceTimestampNs"] = frame.DeviceTimestampNs,
                ["triggerId"] = frame.TriggerId,
                ["triggerSkewUs"] = frameSet.TriggerSkewUs
            }));
    }
}
