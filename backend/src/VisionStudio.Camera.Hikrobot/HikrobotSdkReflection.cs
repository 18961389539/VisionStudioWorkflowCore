using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace VisionStudio.Camera.Hikrobot;

internal static class HikrobotSdkReflection
{
    public static Assembly Load(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var full = Path.GetFullPath(explicitPath);
            if (!File.Exists(full)) throw new FileNotFoundException("Configured Hikrobot MVS .NET assembly was not found.", full);
            return Assembly.LoadFrom(full);
        }
        foreach (var name in new[] { "MvCamCtrl.NET", "MvCameraControl.Net", "MvCameraControl.Net.Standard" })
        {
            try { return Assembly.Load(new AssemblyName(name)); } catch { }
        }
        throw new InvalidOperationException("Hikrobot MVS .NET runtime was not found. Install MVS or configure CameraAdapters:HikrobotAssemblyPath.");
    }

    public static Type MyCamera(Assembly assembly) => assembly.GetType("MvCamCtrl.NET.MyCamera", false)
        ?? assembly.GetTypes().FirstOrDefault(t => t.Name == "MyCamera")
        ?? throw new InvalidOperationException("Hikrobot MyCamera type was not found.");

    public static object? Member(object? instance, string name)
    {
        if (instance is null) return null;
        var t = instance.GetType();
        return t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(instance)
            ?? t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(instance);
    }

    public static void SetMember(object instance, string name, object? value)
    {
        var t = instance.GetType();
        var prop = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop?.CanWrite == true) { prop.SetValue(instance, ConvertValue(value, prop.PropertyType)); return; }
        var field = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (field is not null) field.SetValue(instance, ConvertValue(value, field.FieldType));
    }

    public static string? Text(object? value)
    {
        if (value is null) return null;
        if (value is string s) return Clean(s);
        if (value is char[] chars) return Clean(new string(chars));
        if (value is byte[] bytes)
        {
            var count = Array.IndexOf(bytes, (byte)0); if (count < 0) count = bytes.Length;
            return Clean(System.Text.Encoding.UTF8.GetString(bytes, 0, count));
        }
        return Clean(Convert.ToString(value, CultureInfo.InvariantCulture));
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().TrimEnd('\0');

    public static uint StaticUInt(Type type, string name, uint fallback)
    {
        var value = type.GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? type.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        return value is null ? fallback : Convert.ToUInt32(value, CultureInfo.InvariantCulture);
    }

    public static int StaticInt(Type type, string name, int fallback)
    {
        var value = type.GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? type.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        return value is null ? fallback : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public static IReadOnlyList<(object DeviceInfo, HikIdentity Identity)> Enumerate(Assembly assembly)
    {
        var myCamera = MyCamera(assembly);
        var listType = myCamera.GetNestedType("MV_CC_DEVICE_INFO_LIST", BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Hikrobot MV_CC_DEVICE_INFO_LIST type was not found.");
        var deviceType = myCamera.GetNestedType("MV_CC_DEVICE_INFO", BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Hikrobot MV_CC_DEVICE_INFO type was not found.");
        var method = myCamera.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(x => x.Name == "MV_CC_EnumDevices_NET" && x.GetParameters().Length == 2)
            ?? throw new InvalidOperationException("Hikrobot MV_CC_EnumDevices_NET was not found.");
        var gige = StaticUInt(myCamera, "MV_GIGE_DEVICE", 0x1);
        var usb = StaticUInt(myCamera, "MV_USB_DEVICE", 0x4);
        var list = Activator.CreateInstance(listType)!;
        object?[] args = [gige | usb, list];
        var ret = Convert.ToInt32(method.Invoke(null, args));
        if (ret != 0) throw new InvalidOperationException($"Hikrobot device enumeration failed: 0x{ret:x8}.");
        list = args[1] ?? list;
        var count = Convert.ToInt32(Member(list, "nDeviceNum") ?? 0);
        var pointers = ToPointers(Member(list, "pDeviceInfo")).Take(count);
        var result = new List<(object, HikIdentity)>();
        foreach (var ptr in pointers)
        {
            if (ptr == IntPtr.Zero) continue;
            var info = Marshal.PtrToStructure(ptr, deviceType);
            if (info is null) continue;
            result.Add((info, ReadIdentity(myCamera, info, gige, usb)));
        }
        return result;
    }

    public static (object DeviceInfo, HikIdentity Identity) Select(
        IReadOnlyList<(object DeviceInfo, HikIdentity Identity)> devices,
        string? serial, string? userName, string? deviceKey)
    {
        var matches = devices.Where(x =>
            (string.IsNullOrWhiteSpace(serial) || string.Equals(x.Identity.SerialNumber, serial, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(userName) || string.Equals(x.Identity.UserDefinedName, userName, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(deviceKey) || string.Equals(x.Identity.DeviceKey, deviceKey, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (matches.Length == 0)
            throw new InvalidOperationException($"No Hikrobot camera matched SerialNumber='{serial ?? "*"}', UserDefinedName='{userName ?? "*"}', DeviceKey='{deviceKey ?? "*"}'. Enumerated={devices.Count}.");
        if (matches.Length > 1) throw new InvalidOperationException("Multiple Hikrobot cameras matched. Select a deterministic SerialNumber or DeviceKey.");
        return matches[0];
    }

    private static HikIdentity ReadIdentity(Type myCamera, object deviceInfo, uint gige, uint usb)
    {
        var layer = Convert.ToUInt32(Member(deviceInfo, "nTLayerType") ?? 0u);
        var special = Member(deviceInfo, "SpecialInfo") ?? Member(deviceInfo, "specialInfo");
        if ((layer & gige) != 0)
        {
            var targetType = myCamera.GetNestedType("MV_GIGE_DEVICE_INFO", BindingFlags.Public | BindingFlags.NonPublic);
            var info = ConvertSpecial(special, "stGigEInfo", targetType);
            var serial = Text(Member(info, "chSerialNumber")) ?? string.Empty;
            var ipRaw = Member(info, "nCurrentIp");
            var ip = ipRaw is null ? null : FormatIpv4(Convert.ToUInt32(ipRaw));
            return new HikIdentity(
                Text(Member(info, "chManufacturerName")) ?? "Hikrobot",
                Text(Member(info, "chModelName")) ?? "Hikrobot Camera", serial,
                Text(Member(info, "chDeviceVersion")), Text(Member(info, "chUserDefinedName")),
                "GigE", ip, !string.IsNullOrWhiteSpace(serial) ? "serial:" + serial : "ip:" + (ip ?? "unknown"));
        }
        if ((layer & usb) != 0)
        {
            var targetType = myCamera.GetNestedType("MV_USB3_DEVICE_INFO", BindingFlags.Public | BindingFlags.NonPublic);
            var info = ConvertSpecial(special, "stUsb3VInfo", targetType);
            var serial = Text(Member(info, "chSerialNumber")) ?? string.Empty;
            return new HikIdentity(
                Text(Member(info, "chManufacturerName")) ?? Text(Member(info, "chVendorName")) ?? "Hikrobot",
                Text(Member(info, "chModelName")) ?? "Hikrobot Camera", serial,
                Text(Member(info, "chDeviceVersion")), Text(Member(info, "chUserDefinedName")),
                "USB3", null, !string.IsNullOrWhiteSpace(serial) ? "serial:" + serial : "name:" + (Text(Member(info, "chUserDefinedName")) ?? "unknown"));
        }
        throw new InvalidOperationException($"Unsupported Hikrobot transport layer: 0x{layer:x8}.");
    }

    private static object? ConvertSpecial(object? special, string memberName, Type? targetType)
    {
        if (special is null) return null;
        var raw = Member(special, memberName) ?? special;
        if (targetType is null || targetType.IsInstanceOfType(raw)) return raw;
        if (raw is byte[] bytes)
        {
            var ptr = Marshal.AllocHGlobal(bytes.Length);
            try { Marshal.Copy(bytes, 0, ptr, bytes.Length); return Marshal.PtrToStructure(ptr, targetType); }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        return raw;
    }

    private static IEnumerable<IntPtr> ToPointers(object? raw)
    {
        if (raw is IntPtr[] array) return array;
        if (raw is IEnumerable<IntPtr> seq) return seq;
        if (raw is Array any) return any.Cast<object>().Select(x => x is IntPtr p ? p : IntPtr.Zero);
        return [];
    }

    public static object CreateCamera(Type myCamera, object deviceInfo)
    {
        var camera = Activator.CreateInstance(myCamera) ?? throw new InvalidOperationException("Could not create Hikrobot MyCamera.");
        var create = myCamera.GetMethods().FirstOrDefault(x => x.Name == "MV_CC_CreateDevice_NET" && x.GetParameters().Length == 1)
            ?? throw new InvalidOperationException("Hikrobot MV_CC_CreateDevice_NET was not found.");
        object?[] args = [deviceInfo];
        Check(Convert.ToInt32(create.Invoke(camera, args)), "CreateDevice");
        return camera;
    }

    public static int InvokeInt(object instance, string name, params object?[] args)
    {
        var methods = instance.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(x => x.Name == name).ToArray();
        foreach (var method in methods)
        {
            var ps = method.GetParameters();
            if (ps.Length != args.Length) continue;
            try
            {
                var invokeArgs = new object?[args.Length];
                for (var i = 0; i < args.Length; i++)
                {
                    var target = ps[i].ParameterType.IsByRef ? ps[i].ParameterType.GetElementType()! : ps[i].ParameterType;
                    invokeArgs[i] = ConvertValue(args[i], target);
                }
                var result = Convert.ToInt32(method.Invoke(instance, invokeArgs));
                for (var i = 0; i < args.Length; i++)
                    if (ps[i].ParameterType.IsByRef) args[i] = invokeArgs[i];
                return result;
            }
            catch (ArgumentException) { }
            catch (InvalidCastException) { }
            catch (FormatException) { }
            catch (OverflowException) { }
        }
        throw new MissingMethodException(instance.GetType().FullName, name);
    }

    public static void Check(int result, string operation)
    {
        if (result != 0) throw new InvalidOperationException($"Hikrobot {operation} failed: 0x{result:x8}.");
    }

    public static object? DefaultValue(Type type)
    {
        var target = type.IsByRef ? type.GetElementType()! : type;
        return target.IsValueType ? Activator.CreateInstance(target) : null;
    }

    private static object? ConvertValue(object? value, Type type)
    {
        if (value is null) return null;
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (target.IsInstanceOfType(value)) return value;
        if (target.IsEnum) return value is string s ? Enum.Parse(target, s, true) : Enum.ToObject(target, value);
        return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }

    private static string FormatIpv4(uint ip) => string.Join('.', new[] { (ip >> 24) & 0xff, (ip >> 16) & 0xff, (ip >> 8) & 0xff, ip & 0xff });
}

internal sealed record HikIdentity(string Manufacturer, string Model, string SerialNumber, string? DeviceVersion, string? UserDefinedName, string Transport, string? IpAddress, string DeviceKey);
