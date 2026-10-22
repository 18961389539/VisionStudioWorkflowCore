using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

if (args.Length < 4)
{
    Console.Error.WriteLine("Usage: VisionStudio.Plugin.Packager <packageDir> <output.vspkg> <publisher.pfx> <pfxPassword> [RSA-SHA256|ECDSA-SHA256]");
    return 2;
}

var packageDir = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var pfxPath = Path.GetFullPath(args[2]);
var password = args[3];
var algorithm = args.Length > 4 ? args[4] : "RSA-SHA256";
if (!Directory.Exists(packageDir)) throw new DirectoryNotFoundException(packageDir);
if (!File.Exists(Path.Combine(packageDir, "plugin.json"))) throw new InvalidOperationException("plugin.json is required at package root.");

using var cert = new X509Certificate2(File.ReadAllBytes(pfxPath), password, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
var entries = Directory.GetFiles(packageDir, "*", SearchOption.AllDirectories)
    .Where(x => !string.Equals(Path.GetFileName(x), "signature.json", StringComparison.OrdinalIgnoreCase))
    .ToDictionary(x => Path.GetRelativePath(packageDir, x).Replace('\\','/'), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
entries["publisher.cer"] = cert.Export(X509ContentType.Cert);
var digest = ComputeContentDigest(entries);
var signatureBytes = algorithm.ToUpperInvariant() switch
{
    "RSA-SHA256" => SignRsa(cert, digest),
    "ECDSA-SHA256" => SignEcdsa(cert, digest),
    _ => throw new InvalidOperationException($"Unsupported algorithm '{algorithm}'.")
};
var signature = new
{
    schemaVersion = 1,
    algorithm,
    certificateFile = "publisher.cer",
    contentSha256 = Convert.ToHexString(digest).ToLowerInvariant(),
    signatureBase64 = Convert.ToBase64String(signatureBytes)
};
entries["signature.json"] = JsonSerializer.SerializeToUtf8Bytes(signature, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

Directory.CreateDirectory(Path.GetDirectoryName(output)!);
if (File.Exists(output)) File.Delete(output);
using (var stream = File.Create(output))
using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
{
    foreach (var pair in entries.OrderBy(x => x.Key, StringComparer.Ordinal))
    {
        var entry = zip.CreateEntry(pair.Key, CompressionLevel.Optimal);
        using var target = entry.Open();
        target.Write(pair.Value);
    }
}
Console.WriteLine($"Created {output}");
Console.WriteLine($"Publisher: {cert.Subject}");
Console.WriteLine($"Thumbprint: {cert.Thumbprint}");
Console.WriteLine($"Content SHA-256: {Convert.ToHexString(digest).ToLowerInvariant()}");
return 0;

static byte[] ComputeContentDigest(IReadOnlyDictionary<string, byte[]> entries)
{
    using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    Span<byte> intBuffer = stackalloc byte[4];
    Span<byte> longBuffer = stackalloc byte[8];
    foreach (var pair in entries.Where(x => !string.Equals(x.Key, "signature.json", StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.Key, StringComparer.Ordinal))
    {
        var pathBytes = Encoding.UTF8.GetBytes(pair.Key.Replace('\\','/'));
        var contentDigest = SHA256.HashData(pair.Value);
        BinaryPrimitives.WriteInt32LittleEndian(intBuffer, pathBytes.Length);
        BinaryPrimitives.WriteInt64LittleEndian(longBuffer, pair.Value.LongLength);
        incremental.AppendData(intBuffer);
        incremental.AppendData(pathBytes);
        incremental.AppendData(longBuffer);
        incremental.AppendData(contentDigest);
    }
    return incremental.GetHashAndReset();
}

static byte[] SignRsa(X509Certificate2 cert, byte[] digest)
{
    using var rsa = cert.GetRSAPrivateKey() ?? throw new InvalidOperationException("Certificate has no RSA private key.");
    return rsa.SignHash(digest, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
}

static byte[] SignEcdsa(X509Certificate2 cert, byte[] digest)
{
    using var ecdsa = cert.GetECDsaPrivateKey() ?? throw new InvalidOperationException("Certificate has no ECDSA private key.");
    return ecdsa.SignHash(digest);
}
