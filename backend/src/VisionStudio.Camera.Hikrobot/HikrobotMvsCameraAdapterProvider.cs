using System.Reflection;
using System.Runtime.InteropServices;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Camera.Hikrobot;

public sealed class HikrobotMvsCameraAdapterProvider : ICameraAdapterProvider, ICameraActionCommandProvider
{
    private readonly string? _assemblyPath;
    private readonly Assembly? _assembly;

    public HikrobotMvsCameraAdapterProvider(string? assemblyPath = null)
    {
        _assemblyPath = assemblyPath;
        try { _assembly = HikrobotSdkReflection.Load(assemblyPath); }
        catch (Exception ex) { SdkError = ex.Message; }
    }

    public string Driver => "hikrobot-mvs";
    public string Vendor => "Hikrobot";
    public bool IsSdkAvailable => _assembly is not null;
    public string? SdkError { get; }

    public ValueTask<IReadOnlyList<CameraDiscoveredDevice>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var assembly = _assembly ?? throw new InvalidOperationException(SdkError ?? "Hikrobot MVS SDK unavailable.");
        var result = HikrobotSdkReflection.Enumerate(assembly).Select(x => new CameraDiscoveredDevice(
            Driver, x.Identity.Manufacturer, x.Identity.Model, x.Identity.SerialNumber,
            x.Identity.UserDefinedName, x.Identity.IpAddress, x.Identity.Transport, x.Identity.DeviceKey, null)).ToArray();
        return ValueTask.FromResult<IReadOnlyList<CameraDiscoveredDevice>>(result);
    }

    public ICameraDevice Create(CameraAdapterRegistration registration)
    {
        if (!IsSdkAvailable) throw new InvalidOperationException(SdkError ?? "Hikrobot MVS SDK unavailable.");
        if (string.IsNullOrWhiteSpace(registration.Id)) throw new ArgumentException("Camera Id is required.", nameof(registration));
        return new HikrobotMvsCameraDevice(registration, _assemblyPath);
    }
    public CameraActionCommandCapabilities ActionCommandCapabilities { get; } = new(Immediate: true, Scheduled: true, Acknowledgements: true, DirectedBroadcast: true, ScheduledRequiresTickFrequency: true);

    public Task<CameraActionCommandResult> IssueActionCommandAsync(CameraActionCommandRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var assembly = _assembly ?? throw new InvalidOperationException(SdkError ?? "Hikrobot MVS SDK unavailable.");
        var myCamera = HikrobotSdkReflection.MyCamera(assembly);
        var infoType = myCamera.GetNestedType("MV_ACTION_CMD_INFO", BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Hikrobot MV_ACTION_CMD_INFO type was not found.");
        var resultType = myCamera.GetNestedType("MV_ACTION_CMD_RESULT", BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Hikrobot MV_ACTION_CMD_RESULT type was not found.");
        var listType = myCamera.GetNestedType("MV_ACTION_CMD_RESULT_LIST", BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Hikrobot MV_ACTION_CMD_RESULT_LIST type was not found.");
        var camera = Activator.CreateInstance(myCamera) ?? throw new InvalidOperationException("Could not create Hikrobot MyCamera for Action Command.");
        var info = Activator.CreateInstance(infoType)!;
        var list = Activator.CreateInstance(listType)!;
        var capacity = Math.Max(1, 64);
        var resultSize = Marshal.SizeOf(resultType);
        var ptr = Marshal.AllocHGlobal(resultSize * capacity);
        try
        {
            SetMember(info, "nDeviceKey", unchecked((uint)request.DeviceKey));
            SetMember(info, "nGroupKey", unchecked((uint)request.GroupKey));
            SetMember(info, "nGroupMask", unchecked((uint)request.GroupMask));
            SetMember(info, "pBroadcastAddress", request.BroadcastAddress);
            SetMember(info, "nTimeOut", unchecked((uint)Math.Clamp(request.TimeoutMs, 0, 10000)));
            SetMember(info, "bActionTimeEnable", request.ScheduledDeviceTimeNs is null ? 0u : 1u);
            if (request.ScheduledDeviceTimeNs is { } when)
            {
                var tickHz = request.DeviceTickFrequencyHz is > 0
                    ? request.DeviceTickFrequencyHz.Value
                    : throw new InvalidOperationException("Hikrobot scheduled Action Command requires GevTimestampTickFrequency so normalized nanoseconds can be converted to device ticks.");
                var actionTicks = checked((long)Math.Round((decimal)when * tickHz / 1_000_000_000m));
                SetMember(info, "nActionTime", actionTicks);
            }
            SetMember(list, "pResults", ptr);
            SetMember(list, "nNumResults", 0u); // output field per MVS SDK; pResults buffer is preallocated below

            var method = myCamera.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(x => x.Name == "MV_GIGE_IssueActionCommand_NET" && x.GetParameters().Length == 2)
                ?? throw new InvalidOperationException("Hikrobot MV_GIGE_IssueActionCommand_NET was not found.");
            object?[] args = [info, list];
            var ret = Convert.ToInt32(method.Invoke(camera, args));
            HikrobotSdkReflection.Check(ret, "IssueActionCommand");
            list = args[1] ?? list;
            var count = Convert.ToInt32(HikrobotSdkReflection.Member(list, "nNumResults") ?? 0);
            var messages = new List<string>();
            for (var i = 0; i < Math.Min(count, capacity); i++)
            {
                var item = Marshal.PtrToStructure(IntPtr.Add(ptr, i * resultSize), resultType);
                if (item is null) continue;
                var address = HikrobotSdkReflection.Text(HikrobotSdkReflection.Member(item, "strDeviceAddress")) ?? "device";
                var status = HikrobotSdkReflection.Member(item, "nStatus") ?? HikrobotSdkReflection.Member(item, "nStatusCode");
                messages.Add(status is null ? address : $"{address}:0x{Convert.ToUInt32(status):x4}");
            }
            return Task.FromResult(new CameraActionCommandResult(Driver, request.ScheduledDeviceTimeNs is not null, request.ScheduledDeviceTimeNs, DateTimeOffset.UtcNow, Math.Max(0, count), messages));
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new InvalidOperationException($"Hikrobot action command failed: {ex.InnerException.Message}", ex.InnerException);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private static void SetMember(object instance, string name, object value)
    {
        var type = instance.GetType();
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field is not null) { field.SetValue(instance, ConvertMember(value, field.FieldType)); return; }
        var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (prop?.CanWrite == true) { prop.SetValue(instance, ConvertMember(value, prop.PropertyType)); return; }
        throw new InvalidOperationException($"Hikrobot Action Command field '{name}' was not found.");
    }

    private static object? ConvertMember(object value, Type target)
    {
        var t = Nullable.GetUnderlyingType(target) ?? target;
        if (t.IsInstanceOfType(value)) return value;
        return Convert.ChangeType(value, t, System.Globalization.CultureInfo.InvariantCulture);
    }

}
