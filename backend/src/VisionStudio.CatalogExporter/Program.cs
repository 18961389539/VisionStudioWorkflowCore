using VisionStudio.Engine;

var check = args.Any(x => string.Equals(x, "--check", StringComparison.OrdinalIgnoreCase));
var output = args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal));
if (string.IsNullOrWhiteSpace(output))
{
    Console.Error.WriteLine("Usage: VisionStudio.CatalogExporter [--check] <output-json>");
    return 2;
}

var expected = VisionCatalogSnapshot.Create(BuiltInNodeCatalog.Items);
var fullPath = Path.GetFullPath(output);

if (check)
{
    if (!File.Exists(fullPath))
    {
        Console.Error.WriteLine($"Generated catalog is missing: {fullPath}");
        return 3;
    }

    VisionCatalogDocument? actual;
    try
    {
        actual = VisionCatalogSnapshot.Deserialize(await File.ReadAllTextAsync(fullPath));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Generated catalog cannot be read: {ex.Message}");
        return 4;
    }

    if (actual is null ||
        actual.SchemaVersion != expected.SchemaVersion ||
        !string.Equals(actual.Hash, expected.Hash, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(VisionCatalogSnapshot.ComputeHash(actual.Items), expected.Hash, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Generated frontend fallback catalog is stale. Run scripts/generate-frontend-catalog.* and commit the result.");
        Console.Error.WriteLine($"Expected hash: {expected.Hash}");
        Console.Error.WriteLine($"Actual hash:   {actual?.Hash ?? "<invalid>"}");
        return 5;
    }

    Console.WriteLine($"Catalog fallback is current: schema={expected.SchemaVersion}, nodes={expected.Items.Count}, hash={expected.Hash}");
    return 0;
}

Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
await File.WriteAllTextAsync(fullPath, VisionCatalogSnapshot.Serialize(expected) + Environment.NewLine);
Console.WriteLine($"Generated {fullPath}: schema={expected.SchemaVersion}, nodes={expected.Items.Count}, hash={expected.Hash}");
return 0;
