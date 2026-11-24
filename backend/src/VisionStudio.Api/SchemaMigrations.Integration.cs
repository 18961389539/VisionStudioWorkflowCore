using Microsoft.Data.Sqlite;
using VisionStudio.Api.Security;

namespace VisionStudio.Api;

public sealed partial class SqliteSchemaMigrationRunner
{
    /// <summary>
    /// 服务账号令牌（P3-6）。MES/上位机不应共用操作员账号：令牌按作用域授权、
    /// 只存 SHA-256 哈希（明文仅在创建/轮换时返回一次），支持吊销与轮换。
    /// name 唯一且不区分大小写，保证审计里 "service:&lt;name&gt;" 的归属没有歧义。
    /// </summary>
    private const string ServiceTokenSchemaSql = """
CREATE TABLE IF NOT EXISTS security_service_tokens (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL COLLATE NOCASE UNIQUE,
    token_hash TEXT NOT NULL UNIQUE,
    prefix TEXT NOT NULL,
    scopes TEXT NOT NULL,
    created_at TEXT NOT NULL,
    created_by TEXT NULL,
    expires_at TEXT NULL,
    last_used_at TEXT NULL,
    revoked_at TEXT NULL
);
CREATE INDEX IF NOT EXISTS ix_security_service_tokens_created ON security_service_tokens(created_at DESC);
CREATE INDEX IF NOT EXISTS ix_security_service_tokens_prefix ON security_service_tokens(prefix);
""";

    /// <summary>
    /// 审计哈希链回填。列扩展走 EnsureColumnAsync（恢复旧备份会重放迁移，裸 ALTER 会
    /// duplicate column/ no such table），已有 hash 的行不重算——部分回填也能继续增量。
    /// </summary>
    private static async Task ApplyAuditChainAsync(SqliteConnection connection, SqliteTransaction tx, CancellationToken ct)
    {
        // v3 基线库（早于安全域）没有 audit_events：v2 已被 baseline、不会重放 DDL。
        // 与 v28 登录锁定列同一处理——缺表就跳过，等该库真正拥有审计表后再回填。
        if (!await TableExistsAsync(connection, tx, "audit_events", ct)) return;
        await EnsureColumnAsync(connection, tx, "audit_events", "prev_hash", "TEXT NULL", ct);
        await EnsureColumnAsync(connection, tx, "audit_events", "hash", "TEXT NULL", ct);

        var pending = new List<AuditChainEntry>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = """
SELECT seq,at,user_id,username,role,action,resource,method,path,status_code,success,target,correlation_id,client_ip
FROM audit_events WHERE hash IS NULL ORDER BY seq;
""";
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                pending.Add(new AuditChainEntry(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    NullableString(reader, 2),
                    NullableString(reader, 3),
                    NullableString(reader, 4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetInt32(9),
                    reader.GetBoolean(10),
                    NullableString(reader, 11),
                    NullableString(reader, 12),
                    NullableString(reader, 13)));
            }
        }
        if (pending.Count == 0) return;

        var previous = await ReadLastAuditHashAsync(connection, tx, ct) ?? AuditChain.GenesisHash;
        foreach (var entry in pending)
        {
            var hash = AuditChain.ComputeHash(previous, entry);
            await using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE audit_events SET prev_hash=$prev, hash=$hash WHERE seq=$seq;";
            update.Parameters.AddWithValue("$prev", previous);
            update.Parameters.AddWithValue("$hash", hash);
            update.Parameters.AddWithValue("$seq", entry.Seq);
            await update.ExecuteNonQueryAsync(ct);
            previous = hash;
        }
    }

    private static async Task<string?> ReadLastAuditHashAsync(SqliteConnection connection, SqliteTransaction tx, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT hash FROM audit_events WHERE hash IS NOT NULL ORDER BY seq DESC LIMIT 1;";
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private static string? NullableString(SqliteDataReader reader, int index)
        => reader.IsDBNull(index) ? null : reader.GetString(index);
}
