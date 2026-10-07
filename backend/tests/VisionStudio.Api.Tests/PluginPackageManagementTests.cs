using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace VisionStudio.Api.Tests;

public sealed class PluginPackageManagementTests
{
    [Fact]
    public void V054SignedPackage_PreflightValidatesTrustedPublisherWithoutExecutingPluginCode()
    {
        using var fixture = Fixture.Create();
        using var cert = fixture.CreateCertificate();
        fixture.Service.TrustPublisher(cert.Export(X509ContentType.Cert), "Test Publisher");
        var package = fixture.BuildPackage(cert, "sample.math", "1.0.0");

        var result = fixture.Service.Preflight(package, "sample.math-1.0.0.vspkg");

        Assert.True(result.Valid, result.Error);
        Assert.True(result.Signed);
        Assert.True(result.SignatureValid);
        Assert.True(result.PublisherTrusted);
        Assert.Equal("sample.math", result.Id);
        Assert.Equal("1.0.0", result.Version);
    }

    [Fact]
    public void V054Install_KeepsMultipleVersionsAndStagesUpgradeUntilRestart()
    {
        using var fixture = Fixture.Create();
        using var cert = fixture.CreateCertificate();
        fixture.Service.TrustPublisher(cert.Export(X509ContentType.Cert), "Test Publisher");
        fixture.Service.PrepareRuntimePluginRoot(fixture.PluginRoot);

        var first = fixture.Service.Install(fixture.BuildPackage(cert, "sample.math", "1.0.0"), "v1.vspkg");
        var second = fixture.Service.Install(fixture.BuildPackage(cert, "sample.math", "2.0.0"), "v2.vspkg");

        Assert.True(first.ActivatedImmediately);
        Assert.True(second.RestartRequired);
        Assert.Equal(2, fixture.Service.ListPackages().Count);
        Assert.Contains(fixture.Service.ListPackages(), x => x.Version == "1.0.0" && x.Active);
        Assert.Contains(fixture.Service.ListPackages(), x => x.Version == "2.0.0" && x.Pending);

        fixture.Service.PrepareRuntimePluginRoot(fixture.PluginRoot); // host restart boundary
        Assert.Contains(fixture.Service.ListPackages(), x => x.Version == "2.0.0" && x.Active && !x.Pending);
        var rollback = fixture.Service.Rollback("sample.math", "1.0.0");
        Assert.True(rollback.RestartRequired);
        Assert.Equal("1.0.0", rollback.PendingVersion);
    }

    [Fact]
    public void V054Rollback_CancelsUnappliedPendingUpgradeBeforeRestart()
    {
        using var fixture = Fixture.Create();
        using var cert = fixture.CreateCertificate();
        fixture.Service.TrustPublisher(cert.Export(X509ContentType.Cert), "Test Publisher");
        fixture.Service.PrepareRuntimePluginRoot(fixture.PluginRoot);
        fixture.Service.Install(fixture.BuildPackage(cert, "sample.math", "1.0.0"), "v1.vspkg");
        fixture.Service.Install(fixture.BuildPackage(cert, "sample.math", "2.0.0"), "v2.vspkg");

        var rollback = fixture.Service.Rollback("sample.math", null);

        Assert.False(rollback.RestartRequired);
        Assert.Equal("1.0.0", rollback.ActiveVersion);
        Assert.Null(rollback.PendingVersion);
        Assert.Contains(fixture.Service.ListPackages(), x => x.Version == "1.0.0" && x.Active);
        Assert.DoesNotContain(fixture.Service.ListPackages(), x => x.Pending);
    }

    [Fact]
    public void V054Install_RejectsUnsignedPackageWhenTrustedSignaturePolicyIsEnabled()
    {
        using var fixture = Fixture.Create();
        var package = fixture.BuildUnsignedPackage("sample.math", "1.0.0");
        var preflight = fixture.Service.Preflight(package, "unsigned.vspkg");
        Assert.True(preflight.Valid);
        Assert.False(preflight.Signed);
        Assert.Throws<InvalidOperationException>(() => fixture.Service.Install(package, "unsigned.vspkg"));
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string root)
        {
            Root = root;
            PluginRoot = Path.Combine(root, "plugins");
            var env = new TestEnvironment(root);
            Service = new PluginPackageService(env, Options.Create(new PluginPackageOptions { RequireTrustedSignature = true }), NullLogger<PluginPackageService>.Instance);
        }

        public string Root { get; }
        public string PluginRoot { get; }
        public PluginPackageService Service { get; }

        public static Fixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "visionstudio-plugin-packages-v54-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new Fixture(root);
        }

        public X509Certificate2 CreateCertificate()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=VisionStudio Test Publisher", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            return new X509Certificate2(cert.Export(X509ContentType.Pfx));
        }

        public byte[] BuildPackage(X509Certificate2 cert, string id, string version)
        {
            var entries = BaseEntries(id, version);
            entries["publisher.cer"] = cert.Export(X509ContentType.Cert);
            var contentHash = PluginPackageService.ComputeContentHash(entries);
            using var rsa = cert.GetRSAPrivateKey()!;
            var signature = rsa.SignHash(Convert.FromHexString(contentHash), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            entries["signature.json"] = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                algorithm = "RSA-SHA256",
                certificateFile = "publisher.cer",
                contentSha256 = contentHash,
                signatureBase64 = Convert.ToBase64String(signature)
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return Zip(entries);
        }

        public byte[] BuildUnsignedPackage(string id, string version) => Zip(BaseEntries(id, version));

        private static Dictionary<string, byte[]> BaseEntries(string id, string version)
        {
            var dll = new byte[] { 0x4d, 0x5a, 1, 2, 3, 4 };
            var dllHash = Convert.ToHexString(SHA256.HashData(dll)).ToLowerInvariant();
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 2,
                id,
                name = "Sample Math Plugin",
                version,
                entryAssembly = "Sample.dll",
                enabled = true,
                minimumSdkApiVersion = 2,
                maximumSdkApiVersion = 2,
                vendor = "Tests",
                assemblySha256 = dllHash
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["plugin.json"] = manifest,
                ["Sample.dll"] = dll
            };
        }

        private static byte[] Zip(IReadOnlyDictionary<string, byte[]> entries)
        {
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                foreach (var pair in entries.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    var entry = zip.CreateEntry(pair.Key);
                    using var stream = entry.Open();
                    stream.Write(pair.Value);
                }
            }
            return output.ToArray();
        }

        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "VisionStudio.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(root);
    }
}
