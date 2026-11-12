using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VisionStudio.Api.Hosting;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Security;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api.Diagnostics;

/// <summary>
/// 一键诊断包（2026-11-12）。
///
/// 现场排障此前的动作是"让现场人员把 D:\VisionStudioData 整个拷回来"——目录里混着数据库、
/// 留证图像和日志，既大又可能带出与问题无关的生产数据。这个服务只打包**回答一个问题所需的最小集合**：
/// 版本与环境、健康计数、资产/节点耗时摘要、最近审计、当天日志尾部。
///
/// 边界：
/// <list type="bullet">
/// <item>不打包数据库、留证图像、配方文档——它们体积大且与"为什么这台机器现在不正常"无关；</item>
/// <item>不打包配置原文——appsettings 可能含口令/密钥，宁可让人按 manifest 里的路径自己去取；</item>
/// <item>日志按尾部截断（默认 512 KB），避免一个跑了一周的现场产出几十 MB 的包。</item>
/// </list>
/// 入口只给管理员：审计尾部和日志里会出现用户名、路径与设备地址。
/// </summary>
public sealed class DiagnosticBundleService(
    DiagnosticsCenterService diagnostics,
    RunCounterPersistence runCounters,
    NodeDurationStatistics nodeDurations,
    RollingFileLoggerProvider logs,
    AuditEventStore audits,
    IWebHostEnvironment environment)
{
    /// <summary>单个日志文件最多打包的字节数；超出部分从文件尾部回读。</summary>
    public const int MaxLogBytes = 512 * 1024;

    /// <summary>审计事件条数上限。</summary>
    public const int AuditTailCount = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<byte[]> CreateAsync(CancellationToken cancellationToken = default)
    {
        var dataRoot = VisionStudioDataRoot.Resolve(environment.ContentRootPath);
        var generatedAt = DateTimeOffset.Now;
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteJson(zip, "manifest.json", new
            {
                generatedAt,
                machine = Environment.MachineName,
                processId = Environment.ProcessId,
                os = Environment.OSVersion.ToString(),
                runtime = Environment.Version.ToString(),
                application = AppVersion(),
                contentRoot = environment.ContentRootPath,
                dataRoot,
                logDirectory = Path.Combine(dataRoot, "logs"),
                // 只报计数，不读配置原文：配置可能含密钥，诊断包会被邮件转发。
                logging = new
                {
                    droppedEntries = logs.DroppedEntries,
                    writeFailures = logs.WriteFailures,
                    flushFailures = logs.FlushFailures,
                    drainTimeouts = logs.DrainTimeouts
                },
                auditPersistence = new
                {
                    failures = audits.PersistFailures,
                    lastFailureAt = audits.LastPersistFailureAt,
                    lastFailure = audits.LastPersistFailure
                }
            });

            WriteJson(zip, "diagnostics/summary.json", diagnostics.Summary());
            WriteJson(zip, "diagnostics/assets.json", diagnostics.Assets());
            WriteJson(zip, "diagnostics/run-counters.json", runCounters.Status());
            WriteJson(zip, "diagnostics/node-durations.json", nodeDurations.Snapshot());
            WriteJson(zip, "audit/tail.json", await audits.ListAsync(0, AuditTailCount, null, null, cancellationToken));

            var logPath = LatestLogFile(Path.Combine(dataRoot, "logs"));
            if (logPath is not null)
                await WriteLogTailAsync(zip, logPath, cancellationToken);

            WriteText(zip, "README.txt", """
                VisionStudio 诊断包
                ====================

                内容：
                - manifest.json                 版本、环境、数据根、日志/审计可靠性计数
                - diagnostics/summary.json      资产健康汇总
                - diagnostics/assets.json       资产明细
                - diagnostics/run-counters.json 跨轮计数器落盘状态
                - diagnostics/node-durations.json 每节点耗时分位数
                - audit/tail.json               最近 200 条审计事件
                - logs/*.log                    最近一天日志的尾部（最多 512 KB）

                不含：数据库、留证图像、配方文档、配置文件原文（可能含口令/密钥）。
                需要数据库或配方时，请按 manifest.json 里的 dataRoot 由现场人员单独导出。
                """);
        }

        return buffer.ToArray();
    }

    private static string AppVersion()
    {
        var assembly = typeof(DiagnosticBundleService).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    private static string? LatestLogFile(string directory)
    {
        if (!Directory.Exists(directory)) return null;
        return Directory.EnumerateFiles(directory, "visionstudio-*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static async Task WriteLogTailAsync(ZipArchive zip, string path, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry($"logs/{Path.GetFileName(path)}", CompressionLevel.Optimal);
        await using var target = entry.Open();
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = source.Length;
        var skip = Math.Max(0, length - MaxLogBytes);
        if (skip > 0) source.Seek(skip, SeekOrigin.Begin);

        var buffer = new byte[64 * 1024];
        var first = true;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            var span = buffer.AsMemory(0, read);
            if (first && skip > 0)
            {
                // 从中间截断时丢掉半行，否则日志开头会出现半条 JSON/时间戳，误导排查。
                var newline = span.Span.IndexOf((byte)'\n');
                if (newline < 0) continue;
                span = span[(newline + 1)..];
            }
            first = false;
            await target.WriteAsync(span, cancellationToken);
        }
    }

    private static void WriteJson(ZipArchive zip, string name, object value)
        => WriteText(zip, name, JsonSerializer.Serialize(value, Json));

    private static void WriteText(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }
}
