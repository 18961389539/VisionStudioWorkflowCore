using Microsoft.Extensions.Logging;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api.Tests;

/// <summary>持久滚动文件日志：按天文件、含类别与格式化消息；写入失败不影响主流程。</summary>
public sealed class RollingFileLoggerTests
{
    [Fact]
    public void Logger_WritesDailyFileWithCategoryAndMessage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vs-logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var provider = new RollingFileLoggerProvider(dir, retentionDays: 1))
            {
                var logger = provider.CreateLogger("Test.Category");
                logger.LogInformation("hello {Value}", 42);
                // 异步写入：Dispose 会排空有界队列（await using 作用域结束 → 刷盘完成）。
            }

            var file = Directory.EnumerateFiles(dir, "visionstudio-*.log").Single();
            var text = File.ReadAllText(file);
            Assert.Contains("Test.Category", text);
            Assert.Contains("hello 42", text);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // P2：异常必须保留完整链（类型 + 消息 + 堆栈），而非仅 "Type: Message"。
    [Fact]
    public void Logger_PreservesFullExceptionChain()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vs-logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var provider = new RollingFileLoggerProvider(dir, retentionDays: 1))
            {
                var logger = provider.CreateLogger("Test.Exceptions");
                try { throw new InvalidOperationException("outer-boom", new FormatException("inner-boom")); }
                catch (Exception ex) { logger.LogError(ex, "operation failed"); }
            }

            var text = File.ReadAllText(Directory.EnumerateFiles(dir, "visionstudio-*.log").Single());
            Assert.Contains("operation failed", text);
            Assert.Contains("InvalidOperationException", text);
            Assert.Contains("outer-boom", text);
            // 内部异常 + 堆栈：旧实现只写 "Type: Message" 会丢失这两者。
            Assert.Contains("FormatException", text);
            Assert.Contains("inner-boom", text);
            Assert.Contains("   at ", text);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // P2：单条超长日志必须截断，避免巨型条目撑爆磁盘。
    [Fact]
    public void Logger_TruncatesOverlongLine()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vs-logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var provider = new RollingFileLoggerProvider(dir, retentionDays: 1))
            {
                var logger = provider.CreateLogger("Test.Huge");
                logger.LogInformation(new string('x', RollingFileLoggerProvider.MaxLineLength + 5000));
            }

            var text = File.ReadAllText(Directory.EnumerateFiles(dir, "visionstudio-*.log").Single());
            Assert.Contains("[truncated]", text);
            Assert.True(text.Length < RollingFileLoggerProvider.MaxLineLength + 512, "overlong line must be truncated");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // P2 回归：关闭时队列中尚未落盘的最后一批日志必须被排空，不能丢。
    // 旧实现在 Dispose 里先 Cancel(shutdown) → ReadAllAsync 立即抛 OCE 丢弃队列尾部，
    // 紧接着 Wait(2s) 又可能与取消竞态提前返回——关闭瞬间的关键错误会静默消失。
    [Fact]
    public void Logger_FlushesQueuedEntriesOnDispose()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vs-logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            const int total = 50000; // 远大于正常一次写入循环的处理速度，确保 Dispose 时仍留有积压
            using (var provider = new RollingFileLoggerProvider(dir, retentionDays: 1, queueCapacity: total * 2))
            {
                var logger = provider.CreateLogger("Test.Drain");
                for (var i = 0; i < total; i++) logger.LogInformation("drain-{Index}", i);
                // 不 sleep：立即 Dispose 以最大化"关闭时仍有积压"的概率。
            }

            var text = File.ReadAllText(Directory.EnumerateFiles(dir, "visionstudio-*.log").Single());
            // 第一条与最后一条都必须在：证明队列被完整排空而非头部写完即中断。
            Assert.Contains("drain-0", text);
            Assert.Contains($"drain-{total - 1}", text);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // F10：满队列丢弃必须计数（DropWrite 静默丢弃的缺陷在此回归）。
    [Fact]
    public void Logger_CountsDroppedEntries_WhenQueueIsFull()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vs-logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var provider = new RollingFileLoggerProvider(dir, retentionDays: 1, queueCapacity: 1))
            {
                var logger = provider.CreateLogger("Test.Drops");
                // 同步快速写入远超容量：单线程写盘消费者不可能追上 → 必然发生丢弃。
                for (var i = 0; i < 20000; i++) logger.LogInformation("entry {Index}", i);
                Assert.True(provider.DroppedEntries > 0, "queue overflow was not counted");
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // F10：写入失败（目录不可用）必须计数；构造与 Dispose 都不允许破坏宿主。
    [Fact]
    public void Logger_CountsWriteFailures_WhenLogDirectoryIsUnavailable()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "vs-logs-blocker-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(blocker, "not-a-directory");
            var provider = new RollingFileLoggerProvider(Path.Combine(blocker, "logs"), retentionDays: 1);
            try
            {
                provider.CreateLogger("Test.Failures").LogInformation("this write must fail");
            }
            finally
            {
                provider.Dispose(); // 排空队列：失败在写入线程中被处理并计数
            }
            Assert.True(provider.WriteFailures > 0, "write failure was not counted");
        }
        finally
        {
            try { File.Delete(blocker); } catch { }
        }
    }
}
