using System.Collections;
using System.Globalization;
using System.Reflection;

namespace VisionStudio.Camera.Basler;

internal static class BaslerSdkReflection
{
    public static Assembly Load(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var full = Path.GetFullPath(explicitPath);
            if (!File.Exists(full)) throw new FileNotFoundException("Configured Basler.Pylon assembly was not found.", full);
            return Assembly.LoadFrom(full);
        }
        try { return Assembly.Load(new AssemblyName("Basler.Pylon")); }
        catch (Exception ex) { throw new InvalidOperationException("Basler pylon .NET runtime was not found. Install pylon or configure CameraAdapters:BaslerAssemblyPath.", ex); }
    }

    public static object? Member(object? instance, string name)
    {
        if (instance is null) return null;
        var t = instance.GetType();
        return t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(instance)
            ?? t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(instance);
    }

    public static string? Text(object? value)
    {
        if (value is null) return null;
        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim().TrimEnd('\0');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public static string? DictionaryValue(object? instance, params string[] keys)
    {
        if (instance is null) return null;
        foreach (var key in keys)
        {
            try
            {
                if (instance is IDictionary dict && dict.Contains(key))
                {
                    var value = Text(dict[key]);
                    if (value is not null) return value;
                }
                foreach (var prop in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    var index = prop.GetIndexParameters();
                    if (index.Length != 1 || index[0].ParameterType != typeof(string)) continue;
                    try
                    {
                        var value = Text(prop.GetValue(instance, [key]));
                        if (value is not null) return value;
                    }
                    catch { }
                }
            }
            catch { }
        }
        return null;
    }

    public static IReadOnlyList<object> Enumerate(Assembly assembly)
    {
        var finder = assembly.GetType("Basler.Pylon.CameraFinder", true)!;
        var method = finder.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(x => x.Name == "Enumerate" && x.GetParameters().Length == 0);
        var items = method.Invoke(null, null) as IEnumerable
            ?? throw new InvalidOperationException("Basler CameraFinder.Enumerate returned no enumerable device list.");
        return items.Cast<object>().ToArray();
    }

    public static object Select(IReadOnlyList<object> devices, string? serial, string? userName, string? deviceKey)
    {
        var matches = devices.Where(d =>
        {
            var s = DictionaryValue(d, "SerialNumber");
            var u = DictionaryValue(d, "UserDefinedName", "FriendlyName");
            var k = DeviceKey(d);
            return (string.IsNullOrWhiteSpace(serial) || string.Equals(s, serial, StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrWhiteSpace(userName) || string.Equals(u, userName, StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrWhiteSpace(deviceKey) || string.Equals(k, deviceKey, StringComparison.OrdinalIgnoreCase));
        }).ToArray();
        if (matches.Length == 0)
            throw new InvalidOperationException($"No Basler camera matched SerialNumber='{serial ?? "*"}', UserDefinedName='{userName ?? "*"}', DeviceKey='{deviceKey ?? "*"}'. Enumerated={devices.Count}.");
        if (matches.Length > 1)
            throw new InvalidOperationException("Multiple Basler cameras matched. Select a deterministic SerialNumber or DeviceKey.");
        return matches[0];
    }

    public static string DeviceKey(object cameraInfo)
    {
        var serial = DictionaryValue(cameraInfo, "SerialNumber");
        if (!string.IsNullOrWhiteSpace(serial)) return "serial:" + serial;
        var ip = DictionaryValue(cameraInfo, "IpAddress", "DeviceIpAddress");
        if (!string.IsNullOrWhiteSpace(ip)) return "ip:" + ip;
        return "name:" + (DictionaryValue(cameraInfo, "UserDefinedName", "FriendlyName", "ModelName") ?? "unknown");
    }

    public static object CreateCamera(Assembly assembly, object cameraInfo)
    {
        var type = assembly.GetType("Basler.Pylon.Camera", true)!;
        var ctor = type.GetConstructors().FirstOrDefault(c =>
        {
            var p = c.GetParameters();
            return p.Length == 1 && p[0].ParameterType.IsAssignableFrom(cameraInfo.GetType());
        }) ?? throw new InvalidOperationException("Basler Camera(ICameraInfo) constructor was not found.");
        return ctor.Invoke([cameraInfo]);
    }

    public static object? Indexed(object instance, object key)
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

    public static bool TrySetParameter(object camera, string name, object value)
    {
        try
        {
            var parameters = camera.GetType().GetProperty("Parameters")?.GetValue(camera);
            if (parameters is null) return false;
            var parameter = Indexed(parameters, name);
            if (parameter is null) return false;
            var methods = parameter.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(x => x.Name is "TrySetValue" or "SetValue")
                .OrderBy(x => x.Name == "TrySetValue" ? 0 : 1)
                .ThenBy(x => x.GetParameters().Length);
            foreach (var method in methods)
            {
                var ps = method.GetParameters();
                if (ps.Length < 1 || ps.Length > 2) continue;
                try
                {
                    var converted = ConvertValue(value, ps[0].ParameterType);
                    var args = ps.Length == 1 ? new[] { converted } : new[] { converted, ps[1].HasDefaultValue ? ps[1].DefaultValue : null };
                    var result = method.Invoke(parameter, args);
                    return result is not bool b || b;
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    public static string? TryGetParameterText(object camera, string name)
    {
        try
        {
            var parameters = camera.GetType().GetProperty("Parameters")?.GetValue(camera);
            var parameter = parameters is null ? null : Indexed(parameters, name);
            var method = parameter?.GetType().GetMethod("GetValue", Type.EmptyTypes);
            return Text(method?.Invoke(parameter, null));
        }
        catch { return null; }
    }

    public static bool TryExecuteCommand(object camera, string name)
    {
        try
        {
            var parameters = camera.GetType().GetProperty("Parameters")?.GetValue(camera);
            var parameter = parameters is null ? null : Indexed(parameters, name);
            if (parameter is null) return false;
            foreach (var methodName in new[] { "Execute", "TryExecute" })
            {
                var method = parameter.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(x => x.Name == methodName && x.GetParameters().Length == 0);
                if (method is null) continue;
                var result = method.Invoke(parameter, null);
                return result is not bool b || b;
            }
        }
        catch { }
        return false;
    }

    private static object? ConvertValue(object value, Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (target.IsInstanceOfType(value)) return value;
        if (target.IsEnum) return value is string s ? Enum.Parse(target, s, true) : Enum.ToObject(target, value);
        return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }
}
