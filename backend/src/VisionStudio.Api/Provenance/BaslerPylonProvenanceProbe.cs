using System.Collections;
using System.Reflection;
using VisionStudio.Engine.Provenance;

namespace VisionStudio.Api.Provenance;

/// <summary>
/// Reads Basler identity from an installed pylon .NET runtime without taking a compile-time dependency
/// on a particular pylon package. This keeps the core runtime buildable on machines without pylon.
/// </summary>
public sealed class BaslerPylonProvenanceProbe(
    BaslerPylonProbeOptions options,
    int cacheSeconds) : CachedVendorProvenanceProbe(
        "camera", options.AssetId, "basler-pylon", options.RequiredForProduction, TimeSpan.FromSeconds(Math.Max(1, cacheSeconds)))
{
    protected override ValueTask<HardwareProvenanceData> CaptureCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var assembly = LoadAssembly(options.AssemblyPath, ["Basler.Pylon"])
            ?? throw new InvalidOperationException("Basler pylon .NET assembly was not found. Install pylon or configure VendorProvenance:Basler:AssemblyPath.");
        var finder = assembly.GetType("Basler.Pylon.CameraFinder", throwOnError: false)
            ?? throw new InvalidOperationException("Basler.Pylon.CameraFinder was not found in the loaded pylon assembly.");
        var enumerate = finder.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(x => x.Name == "Enumerate" && x.GetParameters().Length == 0)
            ?? throw new InvalidOperationException("Basler CameraFinder.Enumerate() was not found.");
        var devices = enumerate.Invoke(null, null) as IEnumerable
            ?? throw new InvalidOperationException("Basler CameraFinder.Enumerate() returned no enumerable device list.");

        object? selected = null;
        var seen = 0;
        foreach (var device in devices)
        {
            if (device is null) continue;
            seen++;
            var serial = ReflectionValueReader.DictionaryValue(device, "SerialNumber");
            var userName = ReflectionValueReader.DictionaryValue(device, "UserDefinedName", "FriendlyName");
            var serialMatch = string.IsNullOrWhiteSpace(options.SerialNumber) || string.Equals(serial, options.SerialNumber, StringComparison.OrdinalIgnoreCase);
            var nameMatch = string.IsNullOrWhiteSpace(options.UserDefinedName) || string.Equals(userName, options.UserDefinedName, StringComparison.OrdinalIgnoreCase);
            if (serialMatch && nameMatch)
            {
                if (selected is not null && string.IsNullOrWhiteSpace(options.SerialNumber) && string.IsNullOrWhiteSpace(options.UserDefinedName))
                    throw new InvalidOperationException("Multiple Basler cameras were found. Configure SerialNumber or UserDefinedName for deterministic provenance.");
                selected = device;
            }
        }
        if (selected is null)
            throw new InvalidOperationException($"No Basler camera matched SerialNumber='{options.SerialNumber ?? "*"}', UserDefinedName='{options.UserDefinedName ?? "*"}'. Enumerated={seen}.");

        var vendor = ReflectionValueReader.DictionaryValue(selected, "VendorName", "ManufacturerInfo") ?? "Basler";
        var model = ReflectionValueReader.DictionaryValue(selected, "ModelName");
        var serialNumber = ReflectionValueReader.DictionaryValue(selected, "SerialNumber");
        var deviceVersion = ReflectionValueReader.DictionaryValue(selected, "DeviceVersion");
        var userDefinedName = ReflectionValueReader.DictionaryValue(selected, "UserDefinedName", "FriendlyName");
        var tlType = ReflectionValueReader.DictionaryValue(selected, "TLType", "DeviceClass");
        var ip = ReflectionValueReader.DictionaryValue(selected, "IpAddress", "DeviceIpAddress");
        string? firmware = null;

        // DeviceFirmwareVersion is a GenICam parameter and normally requires opening the camera.
        // Probe it opportunistically; enumeration identity is still useful if exclusive access is unavailable.
        try { firmware = TryReadOpenedStringParameter(assembly, selected, "DeviceFirmwareVersion"); }
        catch { }

        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["liveProbe"] = "basler-pylon",
            ["sdkAssemblyVersion"] = assembly.GetName().Version?.ToString() ?? "unknown"
        };
        if (!string.IsNullOrWhiteSpace(userDefinedName)) attrs["userDefinedName"] = userDefinedName;
        if (!string.IsNullOrWhiteSpace(tlType)) attrs["transportLayer"] = tlType;
        if (!string.IsNullOrWhiteSpace(ip)) attrs["ipAddress"] = ip;

        return ValueTask.FromResult(new HardwareProvenanceData(
            Manufacturer: vendor,
            ProductName: userDefinedName,
            Model: model,
            SerialNumber: serialNumber,
            HardwareRevision: deviceVersion,
            FirmwareVersion: firmware,
            SoftwareVersion: assembly.GetName().Version?.ToString(),
            Attributes: attrs));
    }

    private static string? TryReadOpenedStringParameter(Assembly assembly, object cameraInfo, string parameterName)
    {
        var cameraType = assembly.GetType("Basler.Pylon.Camera", throwOnError: false);
        if (cameraType is null) return null;
        var ctor = cameraType.GetConstructors().FirstOrDefault(c =>
        {
            var p = c.GetParameters();
            return p.Length == 1 && p[0].ParameterType.IsAssignableFrom(cameraInfo.GetType());
        });
        if (ctor is null) return null;
        object? camera = null;
        try
        {
            camera = ctor.Invoke([cameraInfo]);
            cameraType.GetMethod("Open", Type.EmptyTypes)?.Invoke(camera, null);
            var parameters = cameraType.GetProperty("Parameters")?.GetValue(camera);
            if (parameters is null) return null;

            var plc = assembly.GetType("Basler.Pylon.PLCamera", throwOnError: false);
            object key = parameterName;
            if (plc is not null)
            {
                key = plc.GetProperty(parameterName, BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                    ?? plc.GetField(parameterName, BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                    ?? parameterName;
            }
            var parameter = GetIndexed(parameters, key) ?? GetIndexed(parameters, parameterName);
            if (parameter is null) return null;
            var getValue = parameter.GetType().GetMethod("GetValue", Type.EmptyTypes);
            return ReflectionValueReader.Text(getValue?.Invoke(parameter, null));
        }
        finally
        {
            if (camera is not null)
            {
                try { cameraType.GetMethod("Close", Type.EmptyTypes)?.Invoke(camera, null); } catch { }
                if (camera is IDisposable disposable) disposable.Dispose();
            }
        }
    }

    private static object? GetIndexed(object instance, object key)
    {
        foreach (var prop in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var p = prop.GetIndexParameters();
            if (p.Length != 1) continue;
            if (!p[0].ParameterType.IsInstanceOfType(key) && !(p[0].ParameterType == typeof(string) && key is string)) continue;
            try { return prop.GetValue(instance, [key]); } catch { }
        }
        return null;
    }

    internal static Assembly? LoadAssembly(string? explicitPath, IReadOnlyList<string> names)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var full = Path.GetFullPath(explicitPath);
            if (!File.Exists(full)) throw new FileNotFoundException("Configured vendor SDK assembly was not found.", full);
            return Assembly.LoadFrom(full);
        }
        foreach (var name in names)
        {
            try { return Assembly.Load(new AssemblyName(name)); } catch { }
        }
        return null;
    }
}
