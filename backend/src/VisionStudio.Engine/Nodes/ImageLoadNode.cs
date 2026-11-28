using System.Globalization;
using System.Security.Cryptography;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 离线读图（image.load，2026-10-26 · 缺口分析 P1-10）。
///
/// 现状：离线验证/教学/样本跑批只能把目录注册成 <c>FileCameraDevice</c>（必须先配相机），
/// 或者依赖宿主注入 <c>__replayInputPath</c>；节点层没有"读一张本地图"的能力。
///
/// **安全边界（这条不能绕）**：读盘一律经过宿主的 <see cref="IReplayInputResolver"/>，
/// 与 image.acquire 的回放路径共用同一套授权根（数据根 / 媒体库根 / VisionStudio:ReplayInputRoots）。
/// 工作流文档里的路径来自可导入的 JSON，引擎不能直接信任——否则一份恶意流程就能读本机任意文件，
/// 再借 output.httpRequest / output.exportResult 带出去。
///
/// 目录模式按**自然排序**（img2 排在 img10 前面）枚举，而不是文件系统的返回顺序：
/// 跑批必须可复现，否则同一条配方两次运行会拿到不同次序的样本。
/// </summary>
public sealed class ImageLoadNode(IReplayInputResolver? replayInput = null) : IVisionNodeExecutor
{
    public string Type => "image.load";

    private static readonly string[] Extensions = [".png", ".bmp", ".jpg", ".jpeg", ".tif", ".tiff"];

    public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var requested = context.TryGetValue("path", out var connectedPath)
            ? connectedPath.Require<string>("path")
            : node.GetString("path", string.Empty);
        if (string.IsNullOrWhiteSpace(requested))
            throw new InvalidOperationException("image.load requires a non-empty path (file or directory).");
        requested = requested.Trim();

        var maxMegabytes = ProtocolFraming.RequireRange(node.GetInt("maxMegabytes", 64), 1, 4096, "image.load maxMegabytes");
        var recursive = node.GetBool("recursive", false);
        var mode = node.GetString("pathMode", "auto").Trim().ToLowerInvariant();
        if (mode is not ("auto" or "file" or "directory" or "manifest"))
            throw new InvalidOperationException("image.load pathMode must be auto, file, directory or manifest.");
        var pageIndex = ProtocolFraming.RequireRange(node.GetInt("pageIndex", 0), 0, 1000000, "image.load pageIndex");
        var colorModeText = node.GetString("colorMode", "preserve").Trim().ToLowerInvariant();
        var colorMode = colorModeText switch
        {
            "preserve" => ImreadModes.Unchanged,
            "gray" => ImreadModes.Grayscale,
            "color" => ImreadModes.Color,
            _ => throw new InvalidOperationException("image.load colorMode must be preserve, gray or color.")
        };
        var indexPolicy = node.GetString("indexPolicy", "fail").Trim().ToLowerInvariant();
        if (indexPolicy is not ("fail" or "wrap")) throw new InvalidOperationException("image.load indexPolicy must be fail or wrap.");
        var captureManifest = node.GetBool("captureManifest", false);
        if (captureManifest && mode == "manifest")
            throw new InvalidOperationException("image.load captureManifest requires live directory mode.");

        var authorization = replayInput ?? throw new InvalidOperationException(
            "image.load requires host file-read authorization (IReplayInputResolver). " +
            "Offline image reads are a host capability: add the folder to 'VisionStudio:ReplayInputRoots' (or keep it under the data/media root) instead of relying on arbitrary paths.");

        var resolvedRoot = authorization.Resolve(requested) ?? throw new InvalidOperationException(
            $"image.load path '{requested}' is outside the authorized read roots. Add the folder to 'VisionStudio:ReplayInputRoots' to allow it.");
        if (!File.Exists(resolvedRoot) && !Directory.Exists(resolvedRoot))
            throw new FileNotFoundException($"image.load path was not found: '{requested}'.", resolvedRoot);

        var treatAsDirectory = mode switch
        {
            "directory" or "manifest" => true,
            "file" => false,
            _ => Directory.Exists(resolvedRoot)
        };

        long requestedIndex = 0;
        if (context.TryGetValue("sequence", out var connectedSequence))
        {
            requestedIndex = connectedSequence.Require<long>("sequence");
            if (requestedIndex < 0)
                throw new InvalidOperationException($"image.load sequence must be >= 0; got {requestedIndex}.");
        }
        else
        {
            requestedIndex = node.GetInt("index", 0);
            if (requestedIndex < 0)
                throw new InvalidOperationException("image.load index must be >= 0.");
        }

        string fullPath;
        int frameCount;
        long effectiveIndex;
        ReplayImageManifest? manifest = null;
        ReplayImageEntry? manifestEntry = null;
        if (treatAsDirectory)
        {
            if (!Directory.Exists(resolvedRoot))
                throw new DirectoryNotFoundException($"image.load pathMode=directory but the path is not a directory: '{requested}'.");
            List<string>? files = null;
            if (mode == "manifest")
            {
                manifest = ReplayImageManifest.Parse(node.GetString("manifestJson", ""));
                await ReplayImageManifest.ValidateAsync(resolvedRoot, manifest, authorization, maxMegabytes, cancellationToken);
            }
            else
            {
                files = EnumerateFrames(resolvedRoot, recursive, skipLinks: captureManifest);
                if (files.Count == 0)
                    throw new InvalidOperationException($"image.load found no images ({string.Join(", ", Extensions)}) in '{resolvedRoot}'.");
                if (captureManifest)
                    manifest = await ReplayImageManifest.CaptureAsync(resolvedRoot, files, authorization, maxMegabytes, cancellationToken);
            }
            frameCount = manifest?.Files.Length ?? files!.Count;
            effectiveIndex = indexPolicy switch
            {
                "fail" => requestedIndex < frameCount
                    ? requestedIndex
                    : throw new InvalidOperationException($"image.load index {requestedIndex} is out of range: '{resolvedRoot}' has {frameCount} frames."),
                "wrap" => requestedIndex % frameCount,
                _ => throw new InvalidOperationException("image.load indexPolicy must be fail or wrap.")
            };
            manifestEntry = manifest?.Files[(int)effectiveIndex];
            fullPath = manifestEntry is null ? files![(int)effectiveIndex]
                : ReplayImageManifest.ResolveFile(resolvedRoot, manifestEntry.Path, authorization);
        }
        else
        {
            if (!File.Exists(resolvedRoot))
                throw new FileNotFoundException($"image.load file was not found: '{requested}'.", resolvedRoot);
            if (captureManifest)
                throw new InvalidOperationException("image.load requires a directory to capture a replay manifest.");
            if (!IsImageFile(resolvedRoot))
                throw new InvalidOperationException($"image.load only reads {string.Join(", ", Extensions)}; got '{Path.GetFileName(resolvedRoot)}'.");
            if (requestedIndex != 0)
                throw new InvalidOperationException($"image.load file mode reads a single image; sequence/index must be 0 but was {requestedIndex}.");
            frameCount = 1;
            effectiveIndex = 0;
            fullPath = resolvedRoot;
        }

        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        if (pageIndex > 0 && extension is not (".tiff" or ".tif"))
            throw new InvalidOperationException($"image.load pageIndex > 0 requires a TIFF file. Got '{Path.GetFileName(fullPath)}'.");

        Mat mat;
        if (manifestEntry is not null || pageIndex > 0)
        {
            await using var input = OpenBounded(fullPath, maxMegabytes);
            var length = input.Length;
            if (length > Array.MaxLength)
                throw new InvalidOperationException($"image.load file '{Path.GetFileName(fullPath)}' exceeds the memory buffer limit for verified or paged decoding.");
            var bytes = new byte[(int)length];
            await input.ReadExactlyAsync(bytes, cancellationToken);
            if (input.ReadByte() >= 0)
                throw new InvalidDataException($"image.load file '{Path.GetFileName(fullPath)}' changed while being read.");
            if (manifestEntry is not null && !Convert.ToHexString(SHA256.HashData(bytes)).Equals(manifestEntry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"image.load manifest image content changed: '{manifestEntry.Path}'.");
            if (pageIndex > 0) TiffPageSelection.Select(bytes, pageIndex, Path.GetFileName(fullPath), cancellationToken);
            mat = Cv2.ImDecode(bytes, colorMode);
        }
        else
        {
            CheckSize(fullPath, new FileInfo(fullPath).Length, maxMegabytes);
            mat = Cv2.ImRead(fullPath, colorMode);
        }
        if (mat.Empty())
        {
            mat.Dispose();
            throw new InvalidDataException($"image.load could not decode '{Path.GetFileName(fullPath)}'; the file may be truncated or not an image.");
        }

        var image = VisionImage.Own(mat);
        return NodeOutputContracts.CreateOwnedResult(image, () =>
        {
            var sourceMode = mode == "manifest" ? "manifest" : treatAsDirectory ? "directory" : "file";
            var diagnostics = CoordinateValidation.Diagnostics(
                Type,
                ("source", Path.GetFileName(fullPath)),
                ("mode", sourceMode),
                ("sequence", effectiveIndex),
                ("requestedIndex", requestedIndex),
                ("indexPolicy", indexPolicy),
                ("frameCount", frameCount),
                ("manifestVersion", manifest?.Version),
                ("manifestEntryCount", manifest?.Files.Length),
                ("colorMode", colorModeText),
                ("pageIndex", pageIndex),
                ("width", image.Width),
                ("height", image.Height),
                ("channels", image.Mat.Channels()),
                ("bitDepth", BitDepth(image.Mat.Depth())));
            var outputs = new Dictionary<string, VisionValue>
            {
                    ["image"] = VisionValue.Image(image),
                    ["width"] = VisionValue.Integer(image.Width),
                    ["height"] = VisionValue.Integer(image.Height),
                    ["channels"] = VisionValue.Integer(image.Mat.Channels()),
                    ["bitDepth"] = VisionValue.Integer(BitDepth(image.Mat.Depth())),
                    ["fileName"] = VisionValue.String(Path.GetFileName(fullPath)),
                    ["resolvedPath"] = VisionValue.String(fullPath),
                    ["sequence"] = VisionValue.Integer(effectiveIndex),
                    ["frameCount"] = VisionValue.Integer(frameCount),
                    ["pageIndex"] = VisionValue.Integer(pageIndex),
                    ["diagnostics"] = VisionValue.String(diagnostics)
            };
            if (captureManifest && manifest is not null) outputs["manifestJson"] = VisionValue.String(manifest.ToJson());
            return new NodeExecutorResult(outputs,
                new Dictionary<string, object?>
                {
                    ["source"] = Path.GetFileName(fullPath),
                    ["mode"] = sourceMode,
                    ["sequence"] = effectiveIndex,
                    ["requestedIndex"] = requestedIndex,
                    ["indexPolicy"] = indexPolicy,
                    ["frameCount"] = frameCount,
                    ["manifestVersion"] = manifest?.Version,
                    ["colorMode"] = colorModeText,
                    ["pageIndex"] = pageIndex,
                    ["size"] = $"{image.Width}x{image.Height}",
                    ["channels"] = image.Mat.Channels(),
                    ["diagnostics"] = diagnostics
                });
        }, cancellationToken);
    }

    internal static bool IsImageFile(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static void CheckSize(string path, long length, int maxMegabytes)
    {
        if (length > maxMegabytes * 1024L * 1024L)
            throw new InvalidOperationException($"image.load file '{Path.GetFileName(path)}' is {length / (1024d * 1024d):0.#} MB which exceeds maxMegabytes={maxMegabytes}.");
    }

    internal static FileStream OpenBounded(string path, int maxMegabytes)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try { CheckSize(path, file.Length, maxMegabytes); return file; }
        catch { file.Dispose(); throw; }
    }

    /// <summary>按自然排序枚举图像：img2 排在 img10 之前，保证跑批可复现。</summary>
    internal static List<string> EnumerateFrames(string directory, bool recursive, bool skipLinks = false)
    {
        var candidates = skipLinks
            ? Directory.EnumerateFiles(directory, "*", new EnumerationOptions
                { RecurseSubdirectories = recursive, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false })
            : Directory.EnumerateFiles(directory, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
        var files = candidates.Where(IsImageFile).ToList();
        files.Sort(NaturalComparer.Instance);
        return files;
    }

    /// <summary>OpenCV 深度码 → 位深。</summary>
    internal static int BitDepth(MatType depth) => (int)depth switch
    {
        0 or 1 => 8,
        2 or 3 => 16,
        4 or 5 => 32,
        _ => 64
    };

    /// <summary>自然排序比较器：把名字切成"数字段/非数字段"逐段比较。</summary>
    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;

            var leftSpan = left.AsSpan();
            var rightSpan = right.AsSpan();
            int i = 0, j = 0;
            while (i < leftSpan.Length && j < rightSpan.Length)
            {
                if (char.IsDigit(leftSpan[i]) && char.IsDigit(rightSpan[j]))
                {
                    var leftStart = i; var rightStart = j;
                    while (i < leftSpan.Length && char.IsDigit(leftSpan[i])) i++;
                    while (j < rightSpan.Length && char.IsDigit(rightSpan[j])) j++;
                    var leftDigits = leftSpan[leftStart..i].TrimStart('0');
                    var rightDigits = rightSpan[rightStart..j].TrimStart('0');
                    if (leftDigits.Length != rightDigits.Length) return leftDigits.Length.CompareTo(rightDigits.Length);
                    var digits = leftDigits.SequenceCompareTo(rightDigits);
                    if (digits != 0) return digits;
                    continue;
                }
                var comparison = char.ToUpperInvariant(leftSpan[i]).CompareTo(char.ToUpperInvariant(rightSpan[j]));
                if (comparison != 0) return comparison;
                i++; j++;
            }
            var remaining = (leftSpan.Length - i).CompareTo(rightSpan.Length - j);
            return remaining != 0 ? remaining : StringComparer.Ordinal.Compare(left, right);
        }
    }
}
