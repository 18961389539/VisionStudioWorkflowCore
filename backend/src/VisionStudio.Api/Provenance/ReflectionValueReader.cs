using System.Collections;
using System.Globalization;
using System.Reflection;

namespace VisionStudio.Api.Provenance;

internal static class ReflectionValueReader
{
    public static object? Member(object? instance, string name)
    {
        if (instance is null) return null;
        var type = instance.GetType();
        var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop is not null) return prop.GetValue(instance);
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return field?.GetValue(instance);
    }

    public static string? Text(object? value)
    {
        if (value is null) return null;
        if (value is string s) return Clean(s);
        if (value is char[] chars) return Clean(new string(chars).TrimEnd('\0'));
        if (value is byte[] bytes)
        {
            var count = Array.IndexOf(bytes, (byte)0);
            if (count < 0) count = bytes.Length;
            return Clean(System.Text.Encoding.UTF8.GetString(bytes, 0, count));
        }
        if (value is IEnumerable<byte> seq) return Text(seq.ToArray());
        return Clean(Convert.ToString(value, CultureInfo.InvariantCulture));
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
                    var result = Text(dict[key]);
                    if (result is not null) return result;
                }

                var indexers = instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(x => x.GetIndexParameters().Length == 1 && x.GetIndexParameters()[0].ParameterType == typeof(string));
                foreach (var indexer in indexers)
                {
                    try
                    {
                        var result = Text(indexer.GetValue(instance, [key]));
                        if (result is not null) return result;
                    }
                    catch { }
                }
            }
            catch { }
        }
        return null;
    }

    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd('\0');
}
