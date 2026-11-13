namespace VisionStudio.Api;

public sealed partial class SqliteSchemaMigrationRunner
{
    /// <summary>
    /// 报警历史表（2026-11-13）。字段与 <see cref="AlarmRecord"/> 一一对应，时间统一存
    /// ISO-8601 往返字符串。活动报警按 (code, source) 唯一——旧 JSON 实现用"找到就替换"
    /// 维持这条语义，这里改由部分唯一索引兜底，避免并发写入留下两条同时挂着的同类报警。
    /// </summary>
    private const string AlarmHistorySchemaSql = """
CREATE TABLE IF NOT EXISTS alarms (
    id TEXT PRIMARY KEY,
    code TEXT NOT NULL,
    severity TEXT NOT NULL,
    source TEXT NOT NULL,
    message TEXT NOT NULL,
    active INTEGER NOT NULL,
    acknowledged INTEGER NOT NULL,
    raised_at TEXT NOT NULL,
    recovered_at TEXT NULL,
    acknowledged_at TEXT NULL,
    run_id TEXT NULL
);
CREATE INDEX IF NOT EXISTS ix_alarms_raised_at ON alarms(raised_at DESC);
CREATE UNIQUE INDEX IF NOT EXISTS ux_alarms_active_code_source ON alarms(code, source) WHERE active = 1;
""";
}
