using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

public sealed class AcquireImageNode(CameraManager cameraManager, IReplayInputResolver? replayInput = null) : IVisionNodeExecutor
{
    public string Type => "image.acquire";

    public async ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context,
        NodeDefinition node,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));


        var replayInputPath = node.GetString("__replayInputPath", string.Empty);
        if (!string.IsNullOrWhiteSpace(replayInputPath))
        {
            // 回放读盘是宿主能力，不是工作流能力：文档里的路径必须由宿主授权器放行，
            // 否则外部导入的流程 JSON 可以借 image.acquire 读取任意本地文件。
            var full = replayInput?.Resolve(replayInputPath)
                ?? throw new InvalidOperationException(
                    "Offline replay input is not authorized for this run. __replayInputPath is injected by the host replay pipeline; a workflow document cannot enable local file reads.");
            if (!File.Exists(full)) throw new FileNotFoundException("Offline replay input image was not found.", full);
            var mat = OpenCvSharp.Cv2.ImRead(full, OpenCvSharp.ImreadModes.Unchanged);
            if (mat.Empty())
            {
                mat.Dispose();
                throw new InvalidDataException("Offline replay input image could not be decoded.");
            }
            var replayImage = VisionImage.Own(mat);
            return NodeOutputContracts.CreateOwnedResult(replayImage, () =>
            {
                var replaySequenceText = node.GetString("__replaySequence", "0");
                var replaySequence = long.TryParse(replaySequenceText, out var parsedReplaySequence) ? parsedReplaySequence : 0L;
                var replayTimestamp = node.GetString("__replayTimestamp", DateTimeOffset.UtcNow.ToString("O"));
                var replayCameraId = node.GetString("__replayCameraId", "offline-replay");
                var replayDiag = CoordinateValidation.Diagnostics(Type,
                    ("cameraId", replayCameraId), ("sequence", replaySequence), ("replay", true),
                    ("width", replayImage.Width), ("height", replayImage.Height), ("source", Path.GetFileName(full)));
                return new NodeExecutorResult(
                    new Dictionary<string, VisionValue>
                    {
                        ["image"] = VisionValue.Image(replayImage),
                        ["sequence"] = VisionValue.Integer(replaySequence),
                        ["timestamp"] = VisionValue.String(replayTimestamp),
                        ["cameraId"] = VisionValue.String(replayCameraId),
                        ["width"] = VisionValue.Integer(replayImage.Width),
                        ["height"] = VisionValue.Integer(replayImage.Height),
                        ["diagnostics"] = VisionValue.String(replayDiag)
                    },
                    new Dictionary<string, object?>
                    {
                        ["camera"] = replayCameraId,
                        ["source"] = Path.GetFileName(full),
                        ["size"] = $"{replayImage.Width}x{replayImage.Height}",
                        ["replay"] = true,
                        ["diagnostics"] = replayDiag
                    });
            }, cancellationToken);
        }

        var cameraId = node.GetString("cameraId", "virtual-1");
        if (string.IsNullOrWhiteSpace(cameraId)) throw new InvalidOperationException("Acquire Image requires a non-empty cameraId.");
        var timeoutMs = node.GetInt("timeoutMs", 1500);
        if (timeoutMs is < 50 or > 10000) throw new InvalidOperationException("Acquire Image timeoutMs must be between 50 and 10000 ms.");
        var autoStart = node.GetBool("autoStart", node.GetBool("autoOpen", true));
        var triggerBeforeGrab = node.GetBool("triggerBeforeGrab", false);
        var frameModeText = node.GetString("frameMode", "Latest");
        // Previously an unknown mode silently fell back to Latest, so a typo such as
        // frameMode=NextFrame waited on the wrong frame without any signal.
        if (!Enum.TryParse<CameraFrameMode>(frameModeText, true, out var frameMode) || !Enum.IsDefined(frameMode))
            throw new InvalidOperationException($"Unsupported frameMode '{frameModeText}'. Supported values are: {string.Join(", ", Enum.GetNames<CameraFrameMode>())}.");

        // Optional exposure/gain applied through the real device layer before grabbing.
        // Sentinels keep the historical behaviour: exposureUs=0 and gainDb=-1 leave the
        // camera's current settings untouched.
        var exposureUs = node.GetDouble("exposureUs", 0);
        var gainDb = node.GetDouble("gainDb", -1);
        if (!double.IsFinite(exposureUs) || exposureUs < 0 || exposureUs > 10_000_000)
            throw new InvalidOperationException("Acquire Image exposureUs must be 0 (leave unchanged) or between 10 and 10000000.");
        if (!double.IsFinite(gainDb) || gainDb < -1 || gainDb > 48)
            throw new InvalidOperationException("Acquire Image gainDb must be -1 (leave unchanged) or between 0 and 48.");
        if (exposureUs > 0 || gainDb >= 0)
        {
            var device = cameraManager.Require(cameraId);
            if (exposureUs > 0 && !device.Capabilities.Exposure)
                throw new InvalidOperationException($"Camera '{cameraId}' does not support exposure control.");
            if (gainDb >= 0 && !device.Capabilities.Gain)
                throw new InvalidOperationException($"Camera '{cameraId}' does not support gain control.");
            var current = device.Settings;
            await cameraManager.ApplySettingsAsync(cameraId, current with
            {
                ExposureUs = exposureUs > 0 ? exposureUs : current.ExposureUs,
                GainDb = gainDb >= 0 ? gainDb : current.GainDb
            }, cancellationToken);
        }

        var lease = await cameraManager.AcquireAsync(
            cameraId,
            frameMode,
            TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 50, 10000)),
            autoStart,
            triggerBeforeGrab,
            cancellationToken);

        return NodeOutputContracts.CreateOwnedResult(lease, () =>
        {
            var image = VisionImage.FromLease(lease);
            if (node.GetBool("requireNonEmpty", true) && (image.Width < 1 || image.Height < 1 || image.Mat.Empty()))
                throw new InvalidOperationException($"Acquire Image camera '{lease.CameraId}' returned an empty frame.");
            var diag = CoordinateValidation.Diagnostics(Type,
                ("cameraId", lease.CameraId), ("sequence", lease.Sequence), ("frameMode", frameMode.ToString()),
                ("triggerBeforeGrab", triggerBeforeGrab), ("timeoutMs", timeoutMs),
                ("width", image.Width), ("height", image.Height), ("pixelFormat", lease.PixelFormat),
                ("exposureUs", exposureUs), ("gainDb", gainDb));
            return new NodeExecutorResult(
                new Dictionary<string, VisionValue>
                {
                    ["image"] = VisionValue.Image(image),
                    ["sequence"] = VisionValue.Integer(lease.Sequence),
                    ["timestamp"] = VisionValue.String(lease.Timestamp.ToString("O")),
                    ["cameraId"] = VisionValue.String(lease.CameraId),
                    ["width"] = VisionValue.Integer(image.Width),
                    ["height"] = VisionValue.Integer(image.Height),
                    ["diagnostics"] = VisionValue.String(diag)
                },
                new Dictionary<string, object?>
                {
                    ["camera"] = lease.CameraId,
                    ["sequence"] = lease.Sequence,
                    ["timestamp"] = lease.Timestamp.ToString("O"),
                    ["pixelFormat"] = lease.PixelFormat,
                    ["size"] = $"{image.Width}x{image.Height}",
                    ["frameMode"] = frameMode.ToString(),
                    ["triggerBeforeGrab"] = triggerBeforeGrab,
                    ["diagnostics"] = diag
                });
        }, cancellationToken);
    }
}

public sealed class SynchronizedCaptureNode(ISynchronizedFrameSetService synchronization) : IVisionNodeExecutor
{
    public string Type => "camera.syncCapture";

    public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");

        cancellationToken.ThrowIfCancellationRequested();

        if (context is null) throw new ArgumentNullException(nameof(context));
        cancellationToken.ThrowIfCancellationRequested();
if (context is null) throw new ArgumentNullException(nameof(context));

        var groupId = node.GetString("groupId", "sync-group-1").Trim();
        if (string.IsNullOrWhiteSpace(groupId)) throw new InvalidOperationException("camera.syncCapture groupId must be non-empty.");
        if (groupId.Length > 128) throw new InvalidOperationException("camera.syncCapture groupId must be at most 128 characters.");
        var scheduled = node.GetBool("scheduled", false);
        var leadTimeMs = node.GetInt("leadTimeMs", 100);
        if (scheduled && leadTimeMs is < 10 or > 60000) throw new InvalidOperationException("camera.syncCapture leadTimeMs must be between 10 and 60000.");
        var timeoutMs = node.GetInt("frameTimeoutMs", 3000);
        if (timeoutMs is < 100 or > 30000) throw new InvalidOperationException("camera.syncCapture frameTimeoutMs must be between 100 and 30000.");
        var requireWithinTolerance = node.GetBool("requireWithinTolerance", false);
        var minCameraCount = node.GetInt("minCameraCount", 1);
        if (minCameraCount is < 1 or > 64) throw new InvalidOperationException("camera.syncCapture minCameraCount must be between 1 and 64.");
        var maxSkewUs = node.GetDouble("maxSkewUs", 0);
        if (!double.IsFinite(maxSkewUs) || maxSkewUs < 0) throw new InvalidOperationException("camera.syncCapture maxSkewUs must be finite and >= 0 (0 disables).");

        VisionFrameSet frameSet;
        try
        {
            frameSet = await synchronization.CaptureAsync(new SynchronizedFrameSetCaptureRequest(
                groupId, scheduled, leadTimeMs, timeoutMs), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"camera.syncCapture failed for group '{groupId}' (scheduled={scheduled}, leadTimeMs={leadTimeMs}, frameTimeoutMs={timeoutMs}): {ex.Message}", ex);
        }

        var cameraCount = frameSet.Images.Count;
        if (cameraCount == 0)
            throw new InvalidOperationException($"camera.syncCapture group '{frameSet.GroupId}' returned no frames.");
        if (cameraCount < minCameraCount)
            throw new InvalidOperationException(
                $"camera.syncCapture group '{frameSet.GroupId}' returned {cameraCount} camera(s); minCameraCount={minCameraCount}. Partial capture is treated as failure.");
        if (!double.IsFinite(frameSet.TriggerSkewUs) || frameSet.TriggerSkewUs < 0)
            throw new InvalidOperationException($"camera.syncCapture group '{frameSet.GroupId}' returned invalid skew '{frameSet.TriggerSkewUs}'.");
        if (maxSkewUs > 0 && frameSet.TriggerSkewUs > maxSkewUs)
            throw new InvalidOperationException(
                $"camera.syncCapture group '{frameSet.GroupId}' skew {frameSet.TriggerSkewUs:0.###} us exceeds maxSkewUs {maxSkewUs:0.###} us.");
        if (requireWithinTolerance && !frameSet.WithinTolerance)
            throw new InvalidOperationException(
                $"camera.syncCapture group '{frameSet.GroupId}' skew {frameSet.TriggerSkewUs:0.###} us exceeds the configured sync tolerance (requireWithinTolerance=true).");

        var diag = CoordinateValidation.Diagnostics(Type,
            ("groupId", frameSet.GroupId), ("cameraCount", cameraCount), ("triggerSkewUs", frameSet.TriggerSkewUs),
            ("timestampBasis", frameSet.TimestampBasis), ("withinTolerance", frameSet.WithinTolerance),
            ("scheduled", scheduled), ("leadTimeMs", leadTimeMs), ("frameTimeoutMs", timeoutMs),
            ("requireWithinTolerance", requireWithinTolerance), ("minCameraCount", minCameraCount),
            ("cameras", string.Join(",", frameSet.Frames.Keys.Order())));

        return new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["frameSet"] = VisionValue.FrameSet(frameSet),
                ["skewUs"] = VisionValue.Double(frameSet.TriggerSkewUs),
                ["timestampBasis"] = VisionValue.String(frameSet.TimestampBasis),
                ["withinTolerance"] = VisionValue.Boolean(frameSet.WithinTolerance),
                ["cameraCount"] = VisionValue.Integer(cameraCount),
                ["cameras"] = VisionValue.String(string.Join(",", frameSet.Frames.Keys.Order())),
                ["skewMs"] = VisionValue.Double(frameSet.TriggerSkewUs / 1000d),
                ["groupId"] = VisionValue.String(groupId),
                ["diagnostics"] = VisionValue.String(diag)
            },
            new Dictionary<string, object?>
            {
                ["groupId"] = frameSet.GroupId,
                ["cameraCount"] = cameraCount,
                ["triggerSkewUs"] = frameSet.TriggerSkewUs,
                ["timestampBasis"] = frameSet.TimestampBasis,
                ["withinTolerance"] = frameSet.WithinTolerance,
                ["cameras"] = frameSet.Frames.Values.Select(x => new { x.CameraId, x.Sequence, x.DeviceTimestampNs, x.TriggerId }).ToArray(),
                ["diagnostics"] = diag
            });
    }
}

public sealed class FrameSetImageNode : IVisionNodeExecutor
{
    public string Type => "frameset.image";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context is null) throw new ArgumentNullException(nameof(context));
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");

        cancellationToken.ThrowIfCancellationRequested();
        if (context is null) throw new ArgumentNullException(nameof(context));

var frameSet = context.Require<IVisionFrameSet>("frameSet");
        var cameraId = node.GetString("cameraId", "camera-1").Trim();
        if (string.IsNullOrWhiteSpace(cameraId))
            throw new InvalidOperationException("frameset.image requires a non-empty cameraId.");
        if (!frameSet.Images.TryGetValue(cameraId, out var image))
            throw new KeyNotFoundException($"FrameSet '{frameSet.GroupId}' does not contain camera '{cameraId}'. Available: {string.Join(", ", frameSet.Images.Keys.Order())}");
        if (!frameSet.Frames.TryGetValue(cameraId, out var frame))
            throw new InvalidOperationException($"FrameSet '{frameSet.GroupId}' is missing frame metadata for camera '{cameraId}'.");
        if (image.Width < 1 || image.Height < 1)
            throw new InvalidOperationException($"frameset.image camera '{cameraId}' has an empty image.");
        var diag = CoordinateValidation.Diagnostics(Type,
            ("groupId", frameSet.GroupId), ("cameraId", cameraId), ("sequence", frame.Sequence),
            ("width", image.Width), ("height", image.Height), ("triggerSkewUs", frameSet.TriggerSkewUs),
            ("withinTolerance", frameSet.WithinTolerance), ("cameraCount", frameSet.Images.Count));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(image),
                ["sequence"] = VisionValue.Integer(frame.Sequence),
                ["timestamp"] = VisionValue.String(frame.HostTimestamp.ToString("O")),
                ["deviceTimestampNs"] = frame.DeviceTimestampNs is { } ns ? VisionValue.Integer(ns) : new VisionValue(VisionDataType.Integer, null),
                ["triggerId"] = frame.TriggerId is { } triggerId ? VisionValue.Integer(triggerId) : new VisionValue(VisionDataType.Integer, null),
                ["cameraId"] = VisionValue.String(cameraId),
                ["diagnostics"] = VisionValue.String(diag)
            },
            new Dictionary<string, object?>
            {
                ["groupId"] = frameSet.GroupId,
                ["cameraId"] = cameraId,
                ["sequence"] = frame.Sequence,
                ["deviceTimestampNs"] = frame.DeviceTimestampNs,
                ["triggerId"] = frame.TriggerId,
                ["triggerSkewUs"] = frameSet.TriggerSkewUs,
                ["width"] = image.Width,
                ["height"] = image.Height,
                ["diagnostics"] = diag
            }));
    }
}
