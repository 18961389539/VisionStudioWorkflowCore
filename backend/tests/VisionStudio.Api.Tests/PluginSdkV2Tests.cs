using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VisionStudio.Abstractions;
using VisionStudio.Engine;
using VisionStudio.Plugin.Sample;

namespace VisionStudio.Api.Tests;

public sealed class PluginSdkV2Tests
{
    [Fact]
    public void V053ManifestPackage_LoadsWithSdk2ToolIdentityAndPerNodeLifetime()
    {
        using var fixture = PluginFixture.Create(version: "0.54.0");
        using var registry = new VisionNodeRegistry();
        using var manager = new PluginManager(registry, NullLogger<PluginManager>.Instance);

        manager.LoadAll(fixture.Root);

        var plugin = Assert.Single(manager.Plugins);
        Assert.True(plugin.Loaded, plugin.Error);
        Assert.Equal("manifest-v2", plugin.PackageFormat);
        Assert.Equal(2, plugin.SdkApiVersion);
        Assert.False(plugin.RestartRequired);
        var tool = Assert.Single(plugin.Tools!);
        Assert.Equal("math.offset", tool.Type);
        Assert.Equal("2.0.0", tool.ToolVersion);
        Assert.Equal(VisionExecutorLifetime.PerNode, tool.Lifetime);
        Assert.True(registry.IsRegistered("math.offset"));
    }

    [Fact]
    public void V053ManifestDescriptorMismatch_FailsBeforeNodeActivation()
    {
        using var fixture = PluginFixture.Create(version: "9.9.9");
        using var registry = new VisionNodeRegistry();
        using var manager = new PluginManager(registry, NullLogger<PluginManager>.Instance);

        manager.LoadAll(fixture.Root);

        var plugin = Assert.Single(manager.Plugins);
        Assert.False(plugin.Loaded);
        Assert.Contains("does not match", plugin.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.False(registry.IsRegistered("math.offset"));
    }

    [Fact]
    public void V053Rescan_ChangedActiveManifestRequiresRestartInsteadOfHotReplacement()
    {
        using var fixture = PluginFixture.Create(version: "0.54.0");
        using var registry = new VisionNodeRegistry();
        using var manager = new PluginManager(registry, NullLogger<PluginManager>.Instance);
        manager.LoadAll(fixture.Root);
        Assert.True(Assert.Single(manager.Plugins).Loaded);

        var manifestPath = Path.Combine(Path.GetDirectoryName(fixture.AssemblyPath)!, "plugin.json");
        var text = File.ReadAllText(manifestPath);
        File.WriteAllText(manifestPath, text.Replace("VisionStudio SDK Sample", "VisionStudio SDK Sample Updated", StringComparison.Ordinal));

        var result = manager.Rescan();
        var plugin = Assert.Single(result.Plugins);
        Assert.True(plugin.Loaded);
        Assert.True(plugin.RestartRequired);
        Assert.Contains(plugin.Warnings!, x => x.Contains("restart", StringComparison.OrdinalIgnoreCase));
        Assert.True(registry.IsRegistered("math.offset"));
    }

    private sealed class PluginFixture : IDisposable
    {
        private PluginFixture(string root, string assemblyPath) { Root = root; AssemblyPath = assemblyPath; }
        public string Root { get; }
        public string AssemblyPath { get; }

        public static PluginFixture Create(string version)
        {
            var root = Path.Combine(Path.GetTempPath(), "visionstudio-plugin-v53-" + Guid.NewGuid().ToString("N"));
            var package = Path.Combine(root, "sample.math");
            Directory.CreateDirectory(package);
            var sourceAssembly = typeof(SampleMathPlugin).Assembly.Location;
            var assemblyPath = Path.Combine(package, "VisionStudio.Plugin.Sample.dll");
            File.Copy(sourceAssembly, assemblyPath, true);
            var manifest = new
            {
                schemaVersion = 2,
                id = "sample.math",
                name = "Sample Math Plugin",
                version,
                entryAssembly = "VisionStudio.Plugin.Sample.dll",
                enabled = true,
                minimumSdkApiVersion = 2,
                maximumSdkApiVersion = 2,
                vendor = "VisionStudio SDK Sample"
            };
            File.WriteAllText(Path.Combine(package, "plugin.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            return new PluginFixture(root, assemblyPath);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
