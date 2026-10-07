using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace VisionStudio.Api;

public sealed class PluginPackageOptions
{
    public bool RequireTrustedSignature { get; set; } = true;
    public long MaxPackageBytes { get; set; } = 256L * 1024 * 1024;
    public long MaxExpandedBytes { get; set; } = 768L * 1024 * 1024;
    public int MaxEntries { get; set; } = 4096;
}

public sealed record PluginPackageSignatureManifest
{
    public int SchemaVersion { get; init; } = 1;
    public string Algorithm { get; init; } = "RSA-SHA256";
    public string CertificateFile { get; init; } = "publisher.cer";
    public string ContentSha256 { get; init; } = "";
    public string SignatureBase64 { get; init; } = "";
}

public sealed record TrustedPluginPublisher(
    string Thumbprint,
    string Subject,
    string? DisplayName,
    bool Enabled,
    DateTimeOffset AddedAt,
    DateTimeOffset? NotBefore = null,
    DateTimeOffset? NotAfter = null);

public sealed record PluginPackageMetadata(
    string Id,
    string Name,
    string Version,
    string Vendor,
    string PackageSha256,
    string ContentSha256,
    bool Signed,
    bool SignatureValid,
    bool PublisherTrusted,
    string? PublisherThumbprint,
    string? PublisherSubject,
    DateTimeOffset InstalledAt,
    string SourceFileName,
    string PackageFormat = "vspkg-v1");

public sealed record PluginPackageVersionInfo(
    string Id,
    string Name,
    string Version,
    string Vendor,
    string PackageSha256,
    string ContentSha256,
    bool Signed,
    bool SignatureValid,
    bool PublisherTrusted,
    string? PublisherThumbprint,
    string? PublisherSubject,
    DateTimeOffset InstalledAt,
    bool Active,
    bool Pending,
    bool RestartRequired,
    string PackageFormat);

public sealed record PluginPackagePreflightResult(
    bool Valid,
    string? Error,
    string? Id,
    string? Name,
    string? Version,
    string? Vendor,
    string? EntryAssembly,
    string PackageSha256,
    string ContentSha256,
    bool Signed,
    bool SignatureValid,
    bool PublisherTrusted,
    string? PublisherThumbprint,
    string? PublisherSubject,
    IReadOnlyList<string> Warnings);

public sealed record PluginPackageInstallResult(
    PluginPackageVersionInfo Package,
    bool ActivatedImmediately,
    bool RestartRequired,
    string Message);

public sealed record PluginPackageSelectionResult(
    string Id,
    string ActiveVersion,
    string? PendingVersion,
    bool RestartRequired,
    string Message);

internal sealed record PluginPackagePointerState(
    Dictionary<string, string> Active,
    Dictionary<string, string> Pending)
{
    public static PluginPackagePointerState Empty => new(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}

public sealed class PluginPackageService
{
    private const string SignatureFileName = "signature.json";
    private const string MetadataFileName = ".package-metadata.json";
    private const string ArchiveFileName = ".package.vspkg";
    private const string ManagedMarker = ".visionstudio-managed-package";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly object _sync = new();
    private readonly string _repositoryRoot;
    private readonly string _trustRoot;
    private readonly string _trustFile;
    private readonly string _pointerFile;
    private readonly PluginPackageOptions _options;
    private readonly ILogger<PluginPackageService> _logger;
    private string? _pluginsRoot;

    public PluginPackageService(IWebHostEnvironment environment, IOptions<PluginPackageOptions> options, ILogger<PluginPackageService> logger)
    {
        _repositoryRoot = Path.Combine(environment.ContentRootPath, "data", "plugin-packages");
        _trustRoot = Path.Combine(environment.ContentRootPath, "data", "plugin-trust");
        _trustFile = Path.Combine(_trustRoot, "trusted-publishers.json");
        _pointerFile = Path.Combine(_repositoryRoot, "active-pointers.json");
        _options = options.Value;
        _logger = logger;
        Directory.CreateDirectory(_repositoryRoot);
        Directory.CreateDirectory(_trustRoot);
    }

    public IReadOnlyList<TrustedPluginPublisher> ListTrustedPublishers()
    {
        lock (_sync) return ReadTrustedPublishers().OrderBy(x => x.DisplayName ?? x.Subject, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public TrustedPluginPublisher TrustPublisher(byte[] certificateBytes, string? displayName)
    {
        using var certificate = new X509Certificate2(certificateBytes);
        var thumbprint = NormalizeThumbprint(certificate.Thumbprint);
        if (string.IsNullOrWhiteSpace(thumbprint)) throw new InvalidOperationException("Certificate thumbprint is unavailable.");
        if (!certificate.HasPrivateKey && certificate.GetRSAPublicKey() is null && certificate.GetECDsaPublicKey() is null)
            throw new InvalidOperationException("Publisher certificate does not expose an RSA or ECDSA public key.");

        lock (_sync)
        {
            var current = ReadTrustedPublishers().ToDictionary(x => x.Thumbprint, StringComparer.OrdinalIgnoreCase);
            var item = new TrustedPluginPublisher(
                thumbprint,
                certificate.Subject,
                string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
                true,
                DateTimeOffset.UtcNow,
                certificate.NotBefore,
                certificate.NotAfter);
            current[thumbprint] = item;
            WriteJsonAtomic(_trustFile, current.Values.OrderBy(x => x.Thumbprint, StringComparer.OrdinalIgnoreCase).ToArray());
            File.WriteAllBytes(Path.Combine(_trustRoot, thumbprint + ".cer"), certificate.Export(X509ContentType.Cert));
            return item;
        }
    }

    public void RemoveTrustedPublisher(string thumbprint)
    {
        thumbprint = NormalizeThumbprint(thumbprint);
        lock (_sync)
        {
            var next = ReadTrustedPublishers().Where(x => !string.Equals(x.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase)).ToArray();
            WriteJsonAtomic(_trustFile, next);
            var certPath = Path.Combine(_trustRoot, thumbprint + ".cer");
            if (File.Exists(certPath)) File.Delete(certPath);
        }
    }

    public PluginPackagePreflightResult Preflight(byte[] packageBytes, string sourceFileName)
    {
        try
        {
            var inspected = InspectArchive(packageBytes, sourceFileName);
            return inspected.Preflight;
        }
        catch (Exception ex)
        {
            var packageHash = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
            return new PluginPackagePreflightResult(false, ex.Message, null, null, null, null, null, packageHash, "", false, false, false, null, null, []);
        }
    }

    public PluginPackageInstallResult Install(byte[] packageBytes, string sourceFileName)
    {
        lock (_sync)
        {
            var inspected = InspectArchive(packageBytes, sourceFileName);
            if (!inspected.Preflight.Valid) throw new InvalidOperationException(inspected.Preflight.Error ?? "Plugin package preflight failed.");
            if (_options.RequireTrustedSignature && (!inspected.Preflight.Signed || !inspected.Preflight.SignatureValid || !inspected.Preflight.PublisherTrusted))
                throw new InvalidOperationException("Plugin package installation requires a valid signature from a trusted publisher.");

            var manifest = inspected.Manifest;
            var versionDirectory = GetVersionDirectory(manifest.Id, manifest.Version);
            if (Directory.Exists(versionDirectory))
            {
                var existing = ReadMetadata(versionDirectory);
                if (existing is null || !string.Equals(existing.PackageSha256, inspected.Preflight.PackageSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Plugin {manifest.Id} {manifest.Version} is already installed with different package content. Publish a new version instead of mutating an existing version.");
            }
            else
            {
                var temp = versionDirectory + ".install-" + Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(temp);
                try
                {
                    ExtractPayload(inspected.ArchiveEntries, temp);
                    File.WriteAllBytes(Path.Combine(temp, ArchiveFileName), packageBytes);
                    var metadata = new PluginPackageMetadata(
                        manifest.Id,
                        manifest.Name,
                        manifest.Version,
                        manifest.Vendor ?? "",
                        inspected.Preflight.PackageSha256,
                        inspected.Preflight.ContentSha256,
                        inspected.Preflight.Signed,
                        inspected.Preflight.SignatureValid,
                        inspected.Preflight.PublisherTrusted,
                        inspected.Preflight.PublisherThumbprint,
                        inspected.Preflight.PublisherSubject,
                        DateTimeOffset.UtcNow,
                        Path.GetFileName(sourceFileName));
                    WriteJsonAtomic(Path.Combine(temp, MetadataFileName), metadata);
                    Directory.CreateDirectory(Path.GetDirectoryName(versionDirectory)!);
                    Directory.Move(temp, versionDirectory);
                }
                catch
                {
                    if (Directory.Exists(temp)) Directory.Delete(temp, true);
                    throw;
                }
            }

            var pointers = ReadPointers();
            var activated = false;
            var restart = false;
            if (!pointers.Active.TryGetValue(manifest.Id, out var activeVersion))
            {
                if (!string.IsNullOrWhiteSpace(_pluginsRoot))
                {
                    MaterializeVersion(manifest.Id, manifest.Version, _pluginsRoot!);
                    activated = true;
                }
                pointers.Active[manifest.Id] = manifest.Version;
                pointers.Pending.Remove(manifest.Id);
                WritePointers(pointers);
            }
            else if (!string.Equals(activeVersion, manifest.Version, StringComparison.OrdinalIgnoreCase))
            {
                pointers.Pending[manifest.Id] = manifest.Version;
                WritePointers(pointers);
                restart = true;
            }

            var info = ListPackagesCore().Single(x => string.Equals(x.Id, manifest.Id, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Version, manifest.Version, StringComparison.OrdinalIgnoreCase));
            return new PluginPackageInstallResult(info, activated, restart,
                activated ? "Package installed and materialized for runtime discovery." : restart ? $"Package installed. {manifest.Id} {manifest.Version} is pending activation on host restart." : "Package version is already active.");
        }
    }

    public IReadOnlyList<PluginPackageVersionInfo> ListPackages()
    {
        lock (_sync) return ListPackagesCore();
    }

    public PluginPackageSelectionResult SelectVersion(string id, string version)
    {
        lock (_sync)
        {
            EnsureInstalled(id, version);
            var pointers = ReadPointers();
            pointers.Active.TryGetValue(id, out var active);
            if (string.Equals(active, version, StringComparison.OrdinalIgnoreCase))
            {
                pointers.Pending.Remove(id);
                WritePointers(pointers);
                return new PluginPackageSelectionResult(id, version, null, false, "Requested version is already active.");
            }
            if (string.IsNullOrWhiteSpace(active))
            {
                if (!string.IsNullOrWhiteSpace(_pluginsRoot)) MaterializeVersion(id, version, _pluginsRoot!);
                pointers.Active[id] = version;
                pointers.Pending.Remove(id);
                WritePointers(pointers);
                return new PluginPackageSelectionResult(id, version, null, false, "Version selected as the active package.");
            }
            pointers.Pending[id] = version;
            WritePointers(pointers);
            return new PluginPackageSelectionResult(id, active!, version, true, $"Version {version} is staged. Restart the host to switch from {active}.");
        }
    }

    public PluginPackageSelectionResult Rollback(string id, string? targetVersion)
    {
        lock (_sync)
        {
            var packages = ListPackagesCore().Where(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.InstalledAt).ToArray();
            if (packages.Length == 0) throw new KeyNotFoundException($"Plugin package '{id}' is not installed.");
            var pointers = ReadPointers();
            if (!pointers.Active.TryGetValue(id, out var active)) throw new InvalidOperationException($"Plugin '{id}' has no active managed version.");
            var target = targetVersion;
            if (string.IsNullOrWhiteSpace(target) && pointers.Pending.ContainsKey(id))
            {
                pointers.Pending.Remove(id);
                WritePointers(pointers);
                return new PluginPackageSelectionResult(id, active!, null, false, $"Pending version change cancelled. {active} remains active.");
            }
            if (string.IsNullOrWhiteSpace(target))
            {
                var activeInstalledAt = packages.FirstOrDefault(x => string.Equals(x.Version, active, StringComparison.OrdinalIgnoreCase))?.InstalledAt;
                target = packages.FirstOrDefault(x => !string.Equals(x.Version, active, StringComparison.OrdinalIgnoreCase) && (activeInstalledAt is null || x.InstalledAt < activeInstalledAt))?.Version
                    ?? packages.FirstOrDefault(x => !string.Equals(x.Version, active, StringComparison.OrdinalIgnoreCase))?.Version;
            }
            if (string.IsNullOrWhiteSpace(target)) throw new InvalidOperationException($"Plugin '{id}' has no alternate installed version to roll back to.");
            EnsureInstalled(id, target);
            return SelectVersion(id, target);
        }
    }

    public void RemoveVersion(string id, string version)
    {
        lock (_sync)
        {
            var pointers = ReadPointers();
            if (pointers.Active.TryGetValue(id, out var active) && string.Equals(active, version, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cannot remove the active plugin version.");
            if (pointers.Pending.TryGetValue(id, out var pending) && string.Equals(pending, version, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cannot remove a pending plugin version. Select another version first.");
            var directory = GetVersionDirectory(id, version);
            if (!Directory.Exists(directory)) throw new KeyNotFoundException($"Plugin {id} {version} is not installed.");
            Directory.Delete(directory, true);
        }
    }

    /// <summary>Applies pending selections only during host startup, before PluginManager loads any DLL.</summary>
    public void PrepareRuntimePluginRoot(string pluginsRoot)
    {
        lock (_sync)
        {
            _pluginsRoot = Path.GetFullPath(pluginsRoot);
            Directory.CreateDirectory(_pluginsRoot);
            var pointers = ReadPointers();
            foreach (var pair in pointers.Pending.ToArray()) pointers.Active[pair.Key] = pair.Value;
            pointers.Pending.Clear();
            WritePointers(pointers);
            foreach (var pair in pointers.Active) MaterializeVersion(pair.Key, pair.Value, _pluginsRoot);
        }
    }

    private IReadOnlyList<PluginPackageVersionInfo> ListPackagesCore()
    {
        var pointers = ReadPointers();
        var result = new List<PluginPackageVersionInfo>();
        if (!Directory.Exists(_repositoryRoot)) return result;
        foreach (var idDir in Directory.GetDirectories(_repositoryRoot))
        {
            var id = Path.GetFileName(idDir);
            if (id.StartsWith(".", StringComparison.Ordinal)) continue;
            foreach (var versionDir in Directory.GetDirectories(idDir))
            {
                var metadata = ReadMetadata(versionDir);
                if (metadata is null) continue;
                var active = pointers.Active.TryGetValue(metadata.Id, out var av) && string.Equals(av, metadata.Version, StringComparison.OrdinalIgnoreCase);
                var pending = pointers.Pending.TryGetValue(metadata.Id, out var pv) && string.Equals(pv, metadata.Version, StringComparison.OrdinalIgnoreCase);
                var trustedNow = metadata.Signed && !string.IsNullOrWhiteSpace(metadata.PublisherThumbprint) &&
                    ReadTrustedPublishers().Any(x => x.Enabled && string.Equals(x.Thumbprint, metadata.PublisherThumbprint, StringComparison.OrdinalIgnoreCase));
                result.Add(new PluginPackageVersionInfo(metadata.Id, metadata.Name, metadata.Version, metadata.Vendor, metadata.PackageSha256, metadata.ContentSha256,
                    metadata.Signed, metadata.SignatureValid, trustedNow, metadata.PublisherThumbprint, metadata.PublisherSubject, metadata.InstalledAt,
                    active, pending, pending, metadata.PackageFormat));
            }
        }
        return result.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ThenByDescending(x => x.InstalledAt).ToArray();
    }

    private InspectedPackage InspectArchive(byte[] packageBytes, string sourceFileName)
    {
        if (packageBytes.Length == 0) throw new InvalidOperationException("Plugin package is empty.");
        if (packageBytes.LongLength > _options.MaxPackageBytes) throw new InvalidOperationException($"Plugin package exceeds {_options.MaxPackageBytes} bytes.");
        var packageHash = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
        using var memory = new MemoryStream(packageBytes, writable: false);
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count == 0 || archive.Entries.Count > _options.MaxEntries) throw new InvalidOperationException("Plugin package entry count is invalid.");
        var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || entry.FullName.EndsWith('/')) continue;
            var path = NormalizeArchivePath(entry.FullName);
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!entries.TryAdd(path, ReadEntry(entry, ref expanded))) throw new InvalidOperationException($"Duplicate package path '{path}'.");
            if (expanded > _options.MaxExpandedBytes) throw new InvalidOperationException($"Expanded plugin package exceeds {_options.MaxExpandedBytes} bytes.");
        }
        if (!entries.TryGetValue(VisionStudio.Abstractions.VisionPluginSdk.PackageManifestFileName, out var manifestBytes))
            throw new InvalidOperationException(".vspkg requires plugin.json at the archive root.");
        var manifest = JsonSerializer.Deserialize<PluginPackageManifest>(manifestBytes, Json) ?? throw new InvalidOperationException("Could not parse plugin.json.");
        ValidateManifest(manifest, entries);

        var contentHash = ComputeContentHash(entries);
        var signed = entries.TryGetValue(SignatureFileName, out var signatureBytes);
        var signatureValid = false;
        var publisherTrusted = false;
        string? thumbprint = null;
        string? subject = null;
        var warnings = new List<string>();
        if (manifest.IsolationMode == VisionPluginIsolationMode.WorkerProcess)
            warnings.Add($"Package requests WorkerProcess isolation (pool={manifest.WorkerPoolSize?.ToString() ?? "host-default"}, shared-memory threshold={manifest.WorkerSharedMemoryThresholdBytes?.ToString() ?? "host-default"} bytes). Large supported images use file-backed shared memory with PNG fallback; unsupported graph payload types fail closed.");
        if (signed)
        {
            var signature = JsonSerializer.Deserialize<PluginPackageSignatureManifest>(signatureBytes!, Json) ?? throw new InvalidOperationException("Could not parse signature.json.");
            if (signature.SchemaVersion != 1) throw new InvalidOperationException($"Unsupported signature.json schemaVersion {signature.SchemaVersion}.");
            if (!string.Equals(signature.ContentSha256, contentHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("signature.json contentSha256 does not match package content.");
            var certPath = NormalizeArchivePath(signature.CertificateFile);
            if (!entries.TryGetValue(certPath, out var certBytes)) throw new InvalidOperationException($"Publisher certificate '{certPath}' is missing from the package.");
            using var certificate = new X509Certificate2(certBytes);
            thumbprint = NormalizeThumbprint(certificate.Thumbprint);
            subject = certificate.Subject;
            signatureValid = VerifySignature(certificate, signature, Convert.FromHexString(contentHash));
            if (!signatureValid) throw new InvalidOperationException("Plugin package cryptographic signature is invalid.");
            publisherTrusted = ReadTrustedPublishers().Any(x => x.Enabled && string.Equals(x.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase));
            if (!publisherTrusted) warnings.Add($"Publisher {subject} ({thumbprint}) is not trusted by this host.");
            if (DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow > certificate.NotAfter.ToUniversalTime())
                warnings.Add("Publisher certificate is outside its validity period. Signature identity is preserved, but installation policy may reject the package.");
        }
        else warnings.Add("Package is unsigned.");

        if (_options.RequireTrustedSignature && (!signed || !signatureValid || !publisherTrusted)) warnings.Add("Host policy requires a valid signature from a trusted publisher before installation.");
        var result = new PluginPackagePreflightResult(true, null, manifest.Id, manifest.Name, manifest.Version, manifest.Vendor, manifest.EntryAssembly,
            packageHash, contentHash, signed, signatureValid, publisherTrusted, thumbprint, subject, warnings);
        return new InspectedPackage(manifest, entries, result);
    }

    private static void ValidateManifest(PluginPackageManifest manifest, IReadOnlyDictionary<string, byte[]> entries)
    {
        if (manifest.SchemaVersion != VisionStudio.Abstractions.VisionPluginSdk.PackageManifestSchemaVersion)
            throw new InvalidOperationException($"Unsupported plugin.json schemaVersion {manifest.SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.Version))
            throw new InvalidOperationException("plugin.json requires id, name and version.");
        ValidatePathSegment(manifest.Id, "plugin id");
        ValidatePathSegment(manifest.Version, "plugin version");
        var entryAssembly = NormalizeArchivePath(manifest.EntryAssembly);
        if (!entries.TryGetValue(entryAssembly, out var assemblyBytes)) throw new InvalidOperationException($"Entry assembly '{manifest.EntryAssembly}' is missing.");
        if (!string.IsNullOrWhiteSpace(manifest.AssemblySha256))
        {
            var actual = Convert.ToHexString(SHA256.HashData(assemblyBytes)).ToLowerInvariant();
            if (!string.Equals(actual, manifest.AssemblySha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Entry assembly SHA-256 mismatch. Expected {manifest.AssemblySha256}, actual {actual}.");
        }
        if (manifest.IsolationMode != VisionPluginIsolationMode.WorkerProcess &&
            (manifest.WorkerPoolSize is not null || manifest.WorkerSharedMemoryThresholdBytes is not null))
            throw new InvalidOperationException("workerPoolSize/workerSharedMemoryThresholdBytes are only valid for WorkerProcess plugins.");
        if (manifest.WorkerPoolSize is <= 0)
            throw new InvalidOperationException("workerPoolSize must be greater than zero.");
        if (manifest.WorkerSharedMemoryThresholdBytes is < 0)
            throw new InvalidOperationException("workerSharedMemoryThresholdBytes cannot be negative.");
    }

    private static bool VerifySignature(X509Certificate2 certificate, PluginPackageSignatureManifest signature, byte[] digest)
    {
        byte[] signatureBytes;
        try { signatureBytes = Convert.FromBase64String(signature.SignatureBase64); }
        catch (FormatException) { throw new InvalidOperationException("signature.json signatureBase64 is invalid."); }
        if (string.Equals(signature.Algorithm, "RSA-SHA256", StringComparison.OrdinalIgnoreCase))
        {
            using var rsa = certificate.GetRSAPublicKey() ?? throw new InvalidOperationException("Publisher certificate has no RSA public key.");
            return rsa.VerifyHash(digest, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        if (string.Equals(signature.Algorithm, "ECDSA-SHA256", StringComparison.OrdinalIgnoreCase))
        {
            using var ecdsa = certificate.GetECDsaPublicKey() ?? throw new InvalidOperationException("Publisher certificate has no ECDSA public key.");
            return ecdsa.VerifyHash(digest, signatureBytes);
        }
        throw new InvalidOperationException($"Unsupported package signature algorithm '{signature.Algorithm}'.");
    }

    public static string ComputeContentHash(IReadOnlyDictionary<string, byte[]> entries)
    {
        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> intBuffer = stackalloc byte[4];
        Span<byte> longBuffer = stackalloc byte[8];
        foreach (var pair in entries.Where(x => !string.Equals(x.Key, SignatureFileName, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var pathBytes = Encoding.UTF8.GetBytes(pair.Key.Replace('\\', '/'));
            var contentDigest = SHA256.HashData(pair.Value);
            BinaryPrimitives.WriteInt32LittleEndian(intBuffer, pathBytes.Length);
            BinaryPrimitives.WriteInt64LittleEndian(longBuffer, pair.Value.LongLength);
            incremental.AppendData(intBuffer);
            incremental.AppendData(pathBytes);
            incremental.AppendData(longBuffer);
            incremental.AppendData(contentDigest);
        }
        return Convert.ToHexString(incremental.GetHashAndReset()).ToLowerInvariant();
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, ref long expanded)
    {
        using var stream = entry.Open();
        using var output = new MemoryStream();
        stream.CopyTo(output);
        expanded += output.Length;
        return output.ToArray();
    }

    private static string NormalizeArchivePath(string path)
    {
        path = (path ?? "").Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(path)) return "";
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(x => x is "." or "..")) throw new InvalidOperationException($"Unsafe archive path '{path}'.");
        return string.Join('/', parts);
    }

    private static void ExtractPayload(IReadOnlyDictionary<string, byte[]> entries, string destination)
    {
        foreach (var pair in entries)
        {
            if (string.Equals(pair.Key, SignatureFileName, StringComparison.OrdinalIgnoreCase)) continue;
            var path = Path.GetFullPath(Path.Combine(destination, pair.Key.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = destination.EndsWith(Path.DirectorySeparatorChar) ? destination : destination + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Package extraction escaped the staging directory.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, pair.Value);
        }
    }

    private void MaterializeVersion(string id, string version, string pluginsRoot)
    {
        var source = GetVersionDirectory(id, version);
        EnsureInstalled(id, version);
        var target = Path.Combine(pluginsRoot, id);
        if (Directory.Exists(target) && !File.Exists(Path.Combine(target, ManagedMarker)))
            throw new InvalidOperationException($"Managed plugin '{id}' conflicts with unmanaged directory '{target}'. Rename/remove the unmanaged directory before activation.");
        var temp = Path.Combine(pluginsRoot, $".{id}.activate-{Guid.NewGuid():N}");
        CopyDirectory(source, temp, skipMetadata: true);
        File.WriteAllText(Path.Combine(temp, ManagedMarker), $"{id}\n{version}\n");
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(temp, target);
        _logger.LogInformation("Materialized managed plugin {PluginId} {Version} into {PluginDirectory}", id, version, target);
    }

    private static void CopyDirectory(string source, string destination, bool skipMetadata)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            if (skipMetadata && (string.Equals(Path.GetFileName(file), MetadataFileName, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(file), ArchiveFileName, StringComparison.OrdinalIgnoreCase))) continue;
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private void EnsureInstalled(string id, string version)
    {
        if (!Directory.Exists(GetVersionDirectory(id, version))) throw new KeyNotFoundException($"Plugin {id} {version} is not installed.");
    }

    private string GetVersionDirectory(string id, string version)
    {
        ValidatePathSegment(id, "plugin id");
        ValidatePathSegment(version, "plugin version");
        return Path.Combine(_repositoryRoot, id, version);
    }

    private static void ValidatePathSegment(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains('/') || value.Contains('\\') || value.Contains(':'))
            throw new InvalidOperationException($"{label} must be a single safe path segment.");
    }

    private PluginPackageMetadata? ReadMetadata(string versionDirectory)
    {
        var path = Path.Combine(versionDirectory, MetadataFileName);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<PluginPackageMetadata>(File.ReadAllBytes(path), Json);
    }

    private IReadOnlyList<TrustedPluginPublisher> ReadTrustedPublishers()
    {
        if (!File.Exists(_trustFile)) return [];
        return JsonSerializer.Deserialize<TrustedPluginPublisher[]>(File.ReadAllBytes(_trustFile), Json) ?? [];
    }

    private PluginPackagePointerState ReadPointers()
    {
        if (!File.Exists(_pointerFile)) return PluginPackagePointerState.Empty;
        var raw = JsonSerializer.Deserialize<PluginPackagePointerState>(File.ReadAllBytes(_pointerFile), Json) ?? PluginPackagePointerState.Empty;
        return new PluginPackagePointerState(
            new Dictionary<string, string>(raw.Active ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(raw.Pending ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase));
    }

    private void WritePointers(PluginPackagePointerState state) => WriteJsonAtomic(_pointerFile, state);

    private static void WriteJsonAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(value, Json));
        File.Move(temp, path, true);
    }

    private static string NormalizeThumbprint(string? thumbprint) => new((thumbprint ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private sealed record InspectedPackage(PluginPackageManifest Manifest, IReadOnlyDictionary<string, byte[]> ArchiveEntries, PluginPackagePreflightResult Preflight);
}
