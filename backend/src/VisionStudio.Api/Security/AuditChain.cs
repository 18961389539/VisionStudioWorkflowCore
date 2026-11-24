using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VisionStudio.Api.Security;

/// <summary>
/// 审计链中一行参与哈希的字段（不含 seq 之外的行主键元数据）。
/// 规范顺序即协议：调整字段或顺序会让所有历史行校验失败，等同于让历史审计作废。
/// </summary>
public sealed record AuditChainEntry(
    long Seq,
    string At,
    string? UserId,
    string? Username,
    string? Role,
    string Action,
    string Resource,
    string Method,
    string Path,
    int StatusCode,
    bool Success,
    string? Target,
    string? CorrelationId,
    string? ClientIp);

/// <summary>
/// 审计防篡改（P3-7）：每行 hash = SHA-256(JSON 数组[前一行 hash, 本行字段...])，
/// 首行前驱为 64 个 0。本地直接改库（UPDATE/DELETE/插行）都会在下一行或本行校验时暴露。
/// 局限：能改写整条链的攻击者可以重算全部 hash；要发现这类改写需要库外锚点（备份/外部日志），
/// 详见 docs/audit-integrity.md。
/// </summary>
public static class AuditChain
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    public static string ComputeHash(string previousHash, AuditChainEntry entry)
    {
        var canonical = JsonSerializer.Serialize(new object?[]
        {
            previousHash,
            entry.Seq,
            entry.At,
            entry.UserId,
            entry.Username,
            entry.Role,
            entry.Action,
            entry.Resource,
            entry.Method,
            entry.Path,
            entry.StatusCode,
            entry.Success ? 1 : 0,
            entry.Target,
            entry.CorrelationId,
            entry.ClientIp
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}