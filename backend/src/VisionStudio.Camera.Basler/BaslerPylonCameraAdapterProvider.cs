using System.Reflection;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Camera.Basler;

public sealed class BaslerPylonCameraAdapterProvider : ICameraAdapterProvider, ICameraActionCommandProvider
{
    private readonly string? _assemblyPath;
    private readonly Assembly? _assembly;

    public BaslerPylonCameraAdapterProvider(string? assemblyPath = null)
    {
        _assemblyPath = assemblyPath;
        try { _assembly = BaslerSdkReflection.Load(assemblyPath); }
        catch (Exception ex) { SdkError = ex.Message; }
    }

    public string Driver => "basler-pylon";
    public string Vendor => "Basler";
    public bool IsSdkAvailable => _assembly is not null;
    public string? SdkError { get; }

    public ValueTask<IReadOnlyList<CameraDiscoveredDevice>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var assembly = _assembly ?? throw new InvalidOperationException(SdkError ?? "Basler pylon SDK unavailable.");
        var result = BaslerSdkReflection.Enumerate(assembly).Select(device =>
        {
            var model = BaslerSdkReflection.DictionaryValue(device, "ModelName") ?? "Basler Camera";
            var serial = BaslerSdkReflection.DictionaryValue(device, "SerialNumber") ?? string.Empty;
            return new CameraDiscoveredDevice(
                Driver, Vendor, model, serial,
                BaslerSdkReflection.DictionaryValue(device, "UserDefinedName", "FriendlyName"),
                BaslerSdkReflection.DictionaryValue(device, "IpAddress", "DeviceIpAddress"),
                BaslerSdkReflection.DictionaryValue(device, "TLType", "DeviceClass"),
                BaslerSdkReflection.DeviceKey(device),
                FirmwareVersion: null);
        }).ToArray();
        return ValueTask.FromResult<IReadOnlyList<CameraDiscoveredDevice>>(result);
    }

    public ICameraDevice Create(CameraAdapterRegistration registration)
    {
        if (!IsSdkAvailable) throw new InvalidOperationException(SdkError ?? "Basler pylon SDK unavailable.");
        if (string.IsNullOrWhiteSpace(registration.Id)) throw new ArgumentException("Camera Id is required.", nameof(registration));
        return new BaslerPylonCameraDevice(registration, _assemblyPath);
    }
    public CameraActionCommandCapabilities ActionCommandCapabilities { get; } = new(Immediate: true, Scheduled: true, Acknowledgements: false, DirectedBroadcast: true);

    public Task<CameraActionCommandResult> IssueActionCommandAsync(CameraActionCommandRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var assembly = _assembly ?? throw new InvalidOperationException(SdkError ?? "Basler pylon SDK unavailable.");
        var type = assembly.GetType("Basler.Pylon.ActionCommandTrigger", throwOnError: true)!;
        try
        {
            if (request.ScheduledDeviceTimeNs is { } when)
            {
                var method = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(x => x.Name == "Schedule" && x.GetParameters().Length == 5)
                    ?? throw new InvalidOperationException("Basler ActionCommandTrigger.Schedule(deviceKey,groupKey,groupMask,time,broadcast) was not found.");
                method.Invoke(null, [request.DeviceKey, request.GroupKey, request.GroupMask, when, request.BroadcastAddress]);
            }
            else
            {
                var method = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(x => x.Name == "Issue" && x.GetParameters().Length == 4)
                    ?? throw new InvalidOperationException("Basler ActionCommandTrigger.Issue(deviceKey,groupKey,groupMask,broadcast) was not found.");
                method.Invoke(null, [request.DeviceKey, request.GroupKey, request.GroupMask, request.BroadcastAddress]);
            }
            return Task.FromResult(new CameraActionCommandResult(Driver, request.ScheduledDeviceTimeNs is not null, request.ScheduledDeviceTimeNs, DateTimeOffset.UtcNow, 0, ["Basler action command broadcast issued; acknowledgement collection is not enabled by this adapter."]));
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new InvalidOperationException($"Basler action command failed: {ex.InnerException.Message}", ex.InnerException);
        }
    }

}
