using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace VisionStudio.Api.Spc;

/// <summary>
/// 从 result_records 读取 SPC 数据（2026-11-23）。查询走 (dataset, created_at) 索引，
/// 只挑选数值负载：Double / Integer 视为一个样本，NumberList 的每个元素视为一个样本。
/// 其他类型与 NaN/Infinity 一律跳过并计数，不做静默截断。
/// </summary>
public sealed class SpcSeriesReader(SqliteMetadataDatabase database)
{
    public const int MaxDatasetLimit = 500;
    public const int MaxRecordLimit = 10000;
    public const int MaxSamples = 50000;

    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 8 };

    public async Task<IReadOnlyList<SpcDatasetSummary>> ListDatasetsAsync(int limit = 200, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > MaxDatasetLimit)
            throw new ArgumentOutOfRangeException(nameof(limit), $"limit must be within 1..{MaxDatasetLimit}.");

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT dataset,
       COUNT(*),
       COALESCE(SUM(length(CAST(payload_json AS BLOB))), 0),
       MIN(created_at),
       MAX(created_at)
FROM result_records
GROUP BY dataset
ORDER BY MAX(created_at) DESC
LIMIT $limit;
""";
        command.Parameters.AddWithValue("$limit", limit);

        var datasets = new List<SpcDatasetSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            datasets.Add(new SpcDatasetSummary(
                Dataset: reader.GetString(0),
                RecordCount: reader.GetInt32(1),
                PayloadBytes: reader.GetInt64(2),
                FirstRecordAt: ParseTimestamp(reader.IsDBNull(3) ? null : reader.GetString(3)),
                LastRecordAt: ParseTimestamp(reader.IsDBNull(4) ? null : reader.GetString(4))));
        }
        return datasets;
    }

    public async Task<SpcSeries> ReadSeriesAsync(
        string dataset, DateTimeOffset? from, DateTimeOffset? to, int limit = 2000, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dataset))
            throw new ArgumentException("dataset is required.", nameof(dataset));
        if (dataset.Length > 96)
            throw new ArgumentException("dataset must be at most 96 characters.", nameof(dataset));
        if (limit is < 1 or > MaxRecordLimit)
            throw new ArgumentOutOfRangeException(nameof(limit), $"limit must be within 1..{MaxRecordLimit}.");
        if (from is not null && to is not null && from > to)
            throw new ArgumentException("from must not be later than to.", nameof(from));

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT id, record_key, payload_json, created_at
FROM result_records
WHERE dataset = $dataset
  AND ($from IS NULL OR created_at >= $from)
  AND ($to IS NULL OR created_at <= $to)
ORDER BY created_at ASC, rowid ASC
LIMIT $limit;
""";
        command.Parameters.AddWithValue("$dataset", dataset);
        command.Parameters.AddWithValue("$from", (object?)(from?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)) ?? DBNull.Value);
        command.Parameters.AddWithValue("$to", (object?)(to?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)) ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit + 1);

        var samples = new List<SpcSamplePoint>();
        int recordsRead = 0, unsupported = 0, empty = 0, nonFinite = 0;
        var truncated = false;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (recordsRead >= limit)
            {
                truncated = true;
                break;
            }
            recordsRead++;

            var recordId = reader.GetString(0);
            var recordKey = reader.IsDBNull(1) ? null : reader.GetString(1);
            var payload = reader.GetString(2);
            var timestamp = ParseTimestamp(reader.GetString(3)) ?? DateTimeOffset.MinValue;

            var (values, kind) = ReadValues(payload);
            if (kind == PayloadKind.Unsupported) { unsupported++; continue; }
            if (kind == PayloadKind.Empty) { empty++; continue; }

            foreach (var value in values)
            {
                if (samples.Count >= MaxSamples)
                {
                    truncated = true;
                    break;
                }
                if (!double.IsFinite(value)) { nonFinite++; continue; }
                samples.Add(new SpcSamplePoint(timestamp, value, recordId, recordKey));
            }
            if (truncated) break;
        }

        return new SpcSeries(dataset, samples, recordsRead, unsupported, empty, nonFinite, truncated);
    }

    private enum PayloadKind { Numeric, Empty, Unsupported }

    private static (IReadOnlyList<double> Values, PayloadKind Kind) ReadValues(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload, JsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return ([], PayloadKind.Unsupported);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                return ([], PayloadKind.Unsupported);
            if (!root.TryGetProperty("value", out var valueElement))
                return ([], PayloadKind.Unsupported);

            switch (typeElement.GetString())
            {
                case "Double":
                case "Integer":
                    if (valueElement.ValueKind != JsonValueKind.Number || !valueElement.TryGetDouble(out var single))
                        return ([], PayloadKind.Unsupported);
                    return ([single], PayloadKind.Numeric);
                case "NumberList":
                    if (valueElement.ValueKind != JsonValueKind.Array) return ([], PayloadKind.Unsupported);
                    if (valueElement.GetArrayLength() == 0) return ([], PayloadKind.Empty);
                    var values = new List<double>(valueElement.GetArrayLength());
                    foreach (var element in valueElement.EnumerateArray())
                    {
                        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value))
                            values.Add(value);
                        else
                            values.Add(double.NaN); // 计数交给调用方，避免把坏元素当 0
                    }
                    return (values, PayloadKind.Numeric);
                default:
                    return ([], PayloadKind.Unsupported);
            }
        }
        catch (JsonException)
        {
            return ([], PayloadKind.Unsupported);
        }
    }

    private static DateTimeOffset? ParseTimestamp(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;
}