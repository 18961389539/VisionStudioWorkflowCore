using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using VisionStudio.Engine.Provenance;

namespace VisionStudio.Api.Provenance;

/// <summary>
/// Reads Hikrobot MVS identity through the installed MvCamCtrl.NET managed SDK by reflection.
/// No Hikrobot binaries are redistributed with VisionStudio.
/// </summary>
public sealed class HikrobotMvsProvenanceProbe(
    HikrobotMvsProbeOptions options,
    int cacheSeconds) : CachedVendorProvenanceProbe(
        "camera", options.AssetId, "hikrobot-mvs", options.RequiredForProduction, TimeSpan.FromSeconds(Math.Max(1, cacheSeconds)))
{
    protected override ValueTask<HardwareProvenanceData> CaptureCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var assembly = BaslerPylonProvenanceProbe.LoadAssembly(options.AssemblyPath, ["MvCamCtrl.NET", "MvCameraControl.Net", "MvCameraControl.Net.Standard"])
            ?? throw new InvalidOperationException("Hikrobot MVS .NET assembly was not found. Install MVS SDK or configure VendorProvenance:Hikrobot:AssemblyPath.");
        var myCamera = assembly.GetType("MvCamCtrl.NET.MyCamera", false)
            ?? assembly.GetTypes().FirstOrDefault(t => t.Name == "MyCamera")
            ?? throw new InvalidOperationException("Hikrobot MyCamera type was not found in the loaded MVS assembly.");
        var listType = myCamera.GetNestedType("MV_CC_DEVICE_INFO_LIST", BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Hikrobot MV_CC_DEVICE_INFO_LIST type was not found.");
        var deviceInfoType = myCamera.GetNestedType("MV_CC_DEVICE_INFO", BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Hikrobot MV_CC_DEVICE_INFO type was not found.");
        var enumMethod = myCamera.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(x => x.Name == "MV_CC_EnumDevices_NET" && x.GetParameters().Length == 2)
            ?? throw new InvalidOperationException("Hikrobot MV_CC_EnumDevices_NET was not found.");

        var gige = ReadStaticUInt(myCamera, "MV_GIGE_DEVICE", 0x1);
        var usb = ReadStaticUInt(myCamera, "MV_USB_DEVICE", 0x4);
        var list = Activator.CreateInstance(listType) ?? throw new InvalidOperationException("Could not create Hikrobot device list.");
        object?[] args = [gige | usb, list];
        var result = Convert.ToInt32(enumMethod.Invoke(null, args));
        if (result != 0) throw new InvalidOperationException($"Hikrobot device enumeration failed: 0x{result:x8}.");
        list = args[1] ?? list;

        var count = Convert.ToInt32(ReflectionValueReader.Member(list, "nDeviceNum") ?? 0);
        var pointerValue = ReflectionValueReader.Member(list, "pDeviceInfo");
        var pointers = ToPointers(pointerValue).Take(count).ToArray();
        if (pointers.Length == 0) throw new InvalidOperationException("Hikrobot MVS enumerated no GigE/USB cameras.");

        object? selectedDevice = null;
        DeviceIdentity? selectedIdentity = null;
        foreach (var pointer in pointers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pointer == IntPtr.Zero) continue;
            var dev = Marshal.PtrToStructure(pointer, deviceInfoType);
            if (dev is null) continue;
            var identity = ReadIdentity(myCamera, dev, gige, usb);
            var serialMatch = string.IsNullOrWhiteSpace(options.SerialNumber) || string.Equals(identity.SerialNumber, options.SerialNumber, StringComparison.OrdinalIgnoreCase);
            var nameMatch = string.IsNullOrWhiteSpace(options.UserDefinedName) || string.Equals(identity.UserDefinedName, options.UserDefinedName, StringComparison.OrdinalIgnoreCase);
            if (!serialMatch || !nameMatch) continue;
            if (selectedDevice is not null && string.IsNullOrWhiteSpace(options.SerialNumber) && string.IsNullOrWhiteSpace(options.UserDefinedName))
                throw new InvalidOperationException("Multiple Hikrobot cameras were found. Configure SerialNumber or UserDefinedName for deterministic provenance.");
            selectedDevice = dev;
            selectedIdentity = identity;
        }
        if (selectedDevice is null || selectedIdentity is null)
            throw new InvalidOperationException($"No Hikrobot camera matched SerialNumber='{options.SerialNumber ?? "*"}', UserDefinedName='{options.UserDefinedName ?? "*"}'. Enumerated={count}.");

        string? firmware = null;
        try { firmware = TryReadStringNode(myCamera, selectedDevice, "DeviceFirmwareVersion"); } catch { }

        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["liveProbe"] = "hikrobot-mvs",
            ["sdkAssemblyVersion"] = assembly.GetName().Version?.ToString() ?? "unknown",
            ["transportLayer"] = selectedIdentity.TransportLayer
        };
        if (!string.IsNullOrWhiteSpace(selectedIdentity.UserDefinedName)) attrs["userDefinedName"] = selectedIdentity.UserDefinedName;
        if (!string.IsNullOrWhiteSpace(selectedIdentity.IpAddress)) attrs["ipAddress"] = selectedIdentity.IpAddress;

        return ValueTask.FromResult(new HardwareProvenanceData(
            Manufacturer: selectedIdentity.Manufacturer ?? "Hikrobot",
            ProductName: selectedIdentity.UserDefinedName,
            Model: selectedIdentity.Model,
            SerialNumber: selectedIdentity.SerialNumber,
            HardwareRevision: selectedIdentity.DeviceVersion,
            FirmwareVersion: firmware,
            SoftwareVersion: assembly.GetName().Version?.ToString(),
            Attributes: attrs));
    }

    private static DeviceIdentity ReadIdentity(Type myCamera, object deviceInfo, uint gige, uint usb)
    {
        var layer = Convert.ToUInt32(ReflectionValueReader.Member(deviceInfo, "nTLayerType") ?? 0u);
        var special = ReflectionValueReader.Member(deviceInfo, "SpecialInfo") ?? ReflectionValueReader.Member(deviceInfo, "specialInfo");
        if ((layer & gige) != 0)
        {
            var gigeType = myCamera.GetNestedType("MV_GIGE_DEVICE_INFO", BindingFlags.Public | BindingFlags.NonPublic);
            var info = ConvertSpecial(special, "stGigEInfo", gigeType);
            var ipRaw = ReflectionValueReader.Member(info, "nCurrentIp");
            return new DeviceIdentity(
                ReflectionValueReader.Text(ReflectionValueReader.Member(info, "chManufacturerName")),
                ReflectionValueReader.Text(ReflectionValueReader.Member(info, "chModelName")),
                ReflectionValueReader.Text(ReflectionValueReader.Member(info, "chSerialNumber")),
                ReflectionValueReader.Text(ReflectionValueReader.Member(info, "chDeviceVersion")),
                ReflectionValueReader.Text(ReflectionValueReader.Member(info, "chUserDefinedName")),
                "GigE",
                ipRaw is null ? null : FormatIpv4(Convert.ToUInt32(ipRaw)));
        }
        if ((layer & usb) != 0)
        {
            var usbType = myCamera.GetNestedType("MV_USB3_DEVICE_INFO", BindingFlags.Public | BindingFlags.NonPublic);
            var info = ConvertSpecial(special, "stUsb3VInfo", usbType);
            return new DeviceIdentity(
                ReflectionValueReader.Text(ReflectionValueReader.Member(info, "chManufacturerName")) ?? ReflectionValueReader.Text(ReflectionValueReader.Member(info, "chVendorName")),
                ReflectionValueReader.Text(ReflectionValueReader.Member(info, "chModelName")),
                ReflectionValueReader.Text(ReflectionValueReader.Member(info, "chSerialNumber")),
                ReflectionValueReader.Text(ReflectionValueReader.Member(info, "chDeviceVersion")),
                ReflectionValueReader.Text(ReflectionValueReader.Member(info, "chUserDefinedName")),
                "USB3",
                null);
        }
        throw new InvalidOperationException($"Unsupported Hikrobot transport layer: 0x{layer:x8}.");
    }

    private static object? ConvertSpecial(object? special, string memberName, Type? targetType)
    {
        if (special is null) return null;
        var raw = ReflectionValueReader.Member(special, memberName) ?? special;
        if (targetType is null || targetType.IsInstanceOfType(raw)) return raw;
        if (raw is byte[] bytes)
        {
            var ptr = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
                return Marshal.PtrToStructure(ptr, targetType);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        return raw;
    }

    private static string? TryReadStringNode(Type myCamera, object deviceInfo, string key)
    {
        var camera = Activator.CreateInstance(myCamera) ?? throw new InvalidOperationException("Could not create Hikrobot MyCamera.");
        try
        {
            var create = myCamera.GetMethods().FirstOrDefault(x => x.Name == "MV_CC_CreateDevice_NET" && x.GetParameters().Length == 1);
            if (create is null) return null;
            object?[] createArgs = [deviceInfo];
            if (Convert.ToInt32(create.Invoke(camera, createArgs)) != 0) return null;

            var open = myCamera.GetMethods().Where(x => x.Name == "MV_CC_OpenDevice_NET").OrderBy(x => x.GetParameters().Length).FirstOrDefault();
            if (open is null) return null;
            var openArgs = open.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : DefaultValue(p.ParameterType)).ToArray();
            if (Convert.ToInt32(open.Invoke(camera, openArgs)) != 0) return null;

            var get = myCamera.GetMethods().FirstOrDefault(x => x.Name == "MV_CC_GetStringValue_NET" && x.GetParameters().Length == 2);
            if (get is null) return null;
            var valueType = get.GetParameters()[1].ParameterType.GetElementType() ?? get.GetParameters()[1].ParameterType;
            var holder = Activator.CreateInstance(valueType);
            object?[] getArgs = [key, holder];
            if (Convert.ToInt32(get.Invoke(camera, getArgs)) != 0) return null;
            holder = getArgs[1];
            return ReflectionValueReader.Text(ReflectionValueReader.Member(holder, "chCurValue") ?? ReflectionValueReader.Member(holder, "strCurValue"));
        }
        finally
        {
            try { myCamera.GetMethod("MV_CC_CloseDevice_NET", Type.EmptyTypes)?.Invoke(camera, null); } catch { }
            try { myCamera.GetMethod("MV_CC_DestroyDevice_NET", Type.EmptyTypes)?.Invoke(camera, null); } catch { }
            if (camera is IDisposable disposable) disposable.Dispose();
        }
    }

    private static object? DefaultValue(Type type)
    {
        var t = type.IsByRef ? type.GetElementType()! : type;
        return t.IsValueType ? Activator.CreateInstance(t) : null;
    }

    private static uint ReadStaticUInt(Type type, string name, uint fallback)
    {
        var value = type.GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? type.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        return value is null ? fallback : Convert.ToUInt32(value);
    }

    private static IEnumerable<IntPtr> ToPointers(object? value)
    {
        if (value is IntPtr[] typed) return typed;
        if (value is IEnumerable enumerable)
        {
            var output = new List<IntPtr>();
            foreach (var item in enumerable)
            {
                if (item is IntPtr ptr) output.Add(ptr);
                else if (item is not null) output.Add(new IntPtr(Convert.ToInt64(item)));
            }
            return output;
        }
        return [];
    }

    private static string FormatIpv4(uint value) => $"{(value >> 24) & 0xff}.{(value >> 16) & 0xff}.{(value >> 8) & 0xff}.{value & 0xff}";

    private sealed record DeviceIdentity(string? Manufacturer, string? Model, string? SerialNumber, string? DeviceVersion, string? UserDefinedName, string TransportLayer, string? IpAddress);
}
