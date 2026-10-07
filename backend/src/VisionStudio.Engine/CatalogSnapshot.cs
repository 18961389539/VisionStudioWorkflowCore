using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionStudio.Engine;

public sealed record VisionCatalogDocument(
    int SchemaVersion,
    string Hash,
    IReadOnlyList<NodeCatalogItem> Items);

/// <summary>
/// Stable catalog serialization shared by the runtime API and the build-time frontend fallback exporter.
/// The backend registry remains authoritative; the generated frontend document is only an offline bootstrap.
/// </summary>
public static class VisionCatalogSnapshot
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions CanonicalJson = CreateOptions(writeIndented: false);
    private static readonly JsonSerializerOptions PrettyJson = CreateOptions(writeIndented: true);

    public static IReadOnlyList<NodeCatalogItem> DisplayOrder(IEnumerable<NodeCatalogItem> items) => items
        .OrderBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
        .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(x => x.Type, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static IReadOnlyList<NodeCatalogItem> CanonicalOrder(IEnumerable<NodeCatalogItem> items) => items
        .OrderBy(x => x.Type, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static string ComputeHash(IEnumerable<NodeCatalogItem> items)
    {
        var element = JsonSerializer.SerializeToElement(CanonicalOrder(items), CanonicalJson);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            WriteCanonical(writer, element);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    public static VisionCatalogDocument Create(IEnumerable<NodeCatalogItem> items)
    {
        var materialized = DisplayOrder(items);
        return new VisionCatalogDocument(SchemaVersion, ComputeHash(materialized), materialized);
    }

    public static string Serialize(VisionCatalogDocument document, bool indented = true) =>
        JsonSerializer.Serialize(document, indented ? PrettyJson : CanonicalJson);

    public static VisionCatalogDocument? Deserialize(string json) =>
        JsonSerializer.Deserialize<VisionCatalogDocument>(json, CanonicalJson);

    private static JsonSerializerOptions CreateOptions(bool writeIndented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = writeIndented,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
