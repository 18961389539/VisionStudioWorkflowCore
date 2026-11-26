using Microsoft.Data.Sqlite;

namespace VisionStudio.Api.Media;

public sealed record MediaItemQuery(
    string? Collection = null,
    string? Label = null,
    string? Search = null,
    int? MinWidth = null,
    int? MaxWidth = null,
    DateTimeOffset? ModifiedAfter = null,
    DateTimeOffset? ModifiedBefore = null,
    int Skip = 0,
    int Take = 200);

internal sealed class MediaLibraryIndex(SqliteMetadataDatabase database)
{
    private readonly object _gate = new();
    private bool _initialized;

    public void EnsureInitialized(Func<IEnumerable<MediaItemDescriptor>> enumerate)
    {
        if (Volatile.Read(ref _initialized)) return;
        lock (_gate)
        {
            if (_initialized) return;
            Rebuild(enumerate());
            _initialized = true;
        }
    }

    public void Rebuild(IEnumerable<MediaItemDescriptor> items)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var seen = connection.CreateCommand())
        {
            seen.Transaction = transaction;
            seen.CommandText = """
DROP TABLE IF EXISTS temp.media_index_seen;
CREATE TEMP TABLE media_index_seen(relative_path TEXT PRIMARY KEY);
""";
            seen.ExecuteNonQuery();
        }
        foreach (var item in items)
        {
            Upsert(connection, transaction, item);
            using var seen = connection.CreateCommand();
            seen.Transaction = transaction;
            seen.CommandText = "INSERT OR IGNORE INTO temp.media_index_seen(relative_path) VALUES($path);";
            seen.Parameters.AddWithValue("$path", item.RelativePath);
            seen.ExecuteNonQuery();
        }
        using (var remove = connection.CreateCommand())
        {
            remove.Transaction = transaction;
            remove.CommandText = """
DELETE FROM media_index_items
WHERE relative_path NOT IN (SELECT relative_path FROM temp.media_index_seen);
DROP TABLE temp.media_index_seen;
""";
            remove.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void Upsert(MediaItemDescriptor item)
    {
        using var connection = database.OpenConnection();
        Upsert(connection, null, item);
    }

    public void Remove(string relativePath)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM media_index_items WHERE relative_path=$path;";
        command.Parameters.AddWithValue("$path", relativePath);
        command.ExecuteNonQuery();
    }

    public MediaLibraryStatus Status(MediaLibraryStatus limits)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*),
       COUNT(DISTINCT collection_name),
       COALESCE(SUM(CASE WHEN label='NG' THEN 1 ELSE 0 END),0),
       COALESCE(SUM(bytes),0)
FROM media_index_items;
""";
        using var reader = command.ExecuteReader();
        reader.Read();
        return limits with
        {
            CollectionCount = reader.GetInt32(1),
            ItemCount = reader.GetInt32(0),
            NgItemCount = reader.GetInt32(2),
            TotalBytes = reader.GetInt64(3)
        };
    }

    public MediaCollectionDescriptor[] Collections()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT collection_name,
       COUNT(*),
       COALESCE(SUM(CASE WHEN label='NG' THEN 1 ELSE 0 END),0),
       COALESCE(SUM(bytes),0),
       MAX(modified_at)
FROM media_index_items
GROUP BY collection_name
ORDER BY collection_name COLLATE NOCASE;
""";
        using var reader = command.ExecuteReader();
        var result = new List<MediaCollectionDescriptor>();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            result.Add(new MediaCollectionDescriptor(
                name,
                "media://" + name,
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4))));
        }
        return result.ToArray();
    }

    public MediaItemPage Query(MediaItemQuery query)
    {
        using var connection = database.OpenConnection();
        var where = new List<string>();
        using var command = connection.CreateCommand();
        if (!string.IsNullOrWhiteSpace(query.Collection))
        {
            where.Add("collection_name=$collection");
            command.Parameters.AddWithValue("$collection", query.Collection);
        }
        if (!string.IsNullOrWhiteSpace(query.Label))
        {
            where.Add("label=$label");
            command.Parameters.AddWithValue("$label", query.Label);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            where.Add("(file_name LIKE $search ESCAPE '\\' OR relative_path LIKE $search ESCAPE '\\')");
            command.Parameters.AddWithValue("$search", "%" + EscapeLike(query.Search) + "%");
        }
        if (query.MinWidth is not null)
        {
            where.Add("width >= $minWidth");
            command.Parameters.AddWithValue("$minWidth", query.MinWidth);
        }
        if (query.MaxWidth is not null)
        {
            where.Add("width <= $maxWidth");
            command.Parameters.AddWithValue("$maxWidth", query.MaxWidth);
        }
        if (query.ModifiedAfter is not null)
        {
            where.Add("modified_at >= $modifiedAfter");
            command.Parameters.AddWithValue("$modifiedAfter", query.ModifiedAfter.Value.ToString("O"));
        }
        if (query.ModifiedBefore is not null)
        {
            where.Add("modified_at <= $modifiedBefore");
            command.Parameters.AddWithValue("$modifiedBefore", query.ModifiedBefore.Value.ToString("O"));
        }
        var whereSql = where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where);
        command.CommandText = $"SELECT COUNT(*) FROM media_index_items{whereSql};";
        var total = Convert.ToInt32(command.ExecuteScalar());
        command.Parameters.AddWithValue("$skip", Math.Max(0, query.Skip));
        command.Parameters.AddWithValue("$take", Math.Clamp(query.Take, 1, 1000));
        command.CommandText = $"""
SELECT relative_path, collection_name, label, file_name, extension, bytes, modified_at,
       width, height, channels, bit_depth, sha256, origin_path, source_kind, imported_at,
       imported_by, duplicate_of
FROM media_index_items{whereSql}
ORDER BY modified_at DESC, relative_path COLLATE NOCASE
LIMIT $take OFFSET $skip;
""";
        using var reader = command.ExecuteReader();
        var items = new List<MediaItemDescriptor>();
        while (reader.Read())
            items.Add(new MediaItemDescriptor(
                reader.GetString(0),
                "media://" + reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                DateTimeOffset.Parse(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetInt32(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : DateTimeOffset.Parse(reader.GetString(14)),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.IsDBNull(16) ? null : reader.GetString(16)));
        return new MediaItemPage(items, total);
    }

    public IReadOnlyList<MediaItemDescriptor> AllItems()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT relative_path, collection_name, label, file_name, extension, bytes, modified_at,
       width, height, channels, bit_depth, sha256, origin_path, source_kind, imported_at,
       imported_by, duplicate_of
FROM media_index_items;
""";
        using var reader = command.ExecuteReader();
        var items = new List<MediaItemDescriptor>();
        while (reader.Read())
            items.Add(new MediaItemDescriptor(
                reader.GetString(0),
                "media://" + reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                DateTimeOffset.Parse(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetInt32(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : DateTimeOffset.Parse(reader.GetString(14)),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.IsDBNull(16) ? null : reader.GetString(16)));
        return items;
    }

    public string? FindDuplicate(string sha256, string? relativePath = null)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT relative_path
FROM media_index_items
WHERE sha256=$sha256 AND ($path IS NULL OR relative_path<>$path)
ORDER BY relative_path COLLATE NOCASE
LIMIT 1;
""";
        command.Parameters.AddWithValue("$sha256", sha256);
        command.Parameters.AddWithValue("$path", (object?)relativePath ?? DBNull.Value);
        return command.ExecuteScalar() as string;
    }

    private static void Upsert(SqliteConnection connection, SqliteTransaction? transaction, MediaItemDescriptor item)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
INSERT INTO media_index_items(
    relative_path, collection_name, label, file_name, extension, bytes, modified_at,
    width, height, channels, bit_depth, sha256, origin_path, source_kind, imported_at,
    imported_by, duplicate_of, indexed_at)
VALUES(
    $path,$collection,$label,$name,$extension,$bytes,$modified,$width,$height,$channels,$bitDepth,
    $sha256,$originPath,$sourceKind,$importedAt,$importedBy,$duplicateOf,$indexed)
ON CONFLICT(relative_path) DO UPDATE SET
    collection_name=excluded.collection_name,
    label=excluded.label,
    file_name=excluded.file_name,
    extension=excluded.extension,
    bytes=excluded.bytes,
    modified_at=excluded.modified_at,
    width=COALESCE(excluded.width,media_index_items.width),
    height=COALESCE(excluded.height,media_index_items.height),
    channels=COALESCE(excluded.channels,media_index_items.channels),
    bit_depth=COALESCE(excluded.bit_depth,media_index_items.bit_depth),
    sha256=COALESCE(excluded.sha256,media_index_items.sha256),
    origin_path=COALESCE(excluded.origin_path,media_index_items.origin_path),
    source_kind=COALESCE(excluded.source_kind,media_index_items.source_kind),
    imported_at=COALESCE(excluded.imported_at,media_index_items.imported_at),
    imported_by=COALESCE(excluded.imported_by,media_index_items.imported_by),
    duplicate_of=CASE WHEN excluded.sha256 IS NULL THEN media_index_items.duplicate_of ELSE excluded.duplicate_of END,
    indexed_at=excluded.indexed_at;
""";
        command.Parameters.AddWithValue("$path", item.RelativePath);
        command.Parameters.AddWithValue("$collection", item.Collection);
        command.Parameters.AddWithValue("$label", item.Label);
        command.Parameters.AddWithValue("$name", item.Name);
        command.Parameters.AddWithValue("$extension", item.Extension);
        command.Parameters.AddWithValue("$bytes", item.Bytes);
        command.Parameters.AddWithValue("$modified", item.ModifiedAt.ToString("O"));
        command.Parameters.AddWithValue("$width", (object?)item.Width ?? DBNull.Value);
        command.Parameters.AddWithValue("$height", (object?)item.Height ?? DBNull.Value);
        command.Parameters.AddWithValue("$channels", (object?)item.Channels ?? DBNull.Value);
        command.Parameters.AddWithValue("$bitDepth", (object?)item.BitDepth ?? DBNull.Value);
        command.Parameters.AddWithValue("$sha256", (object?)item.Sha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$originPath", (object?)item.OriginPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceKind", (object?)item.SourceKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$importedAt", (object?)item.ImportedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$importedBy", (object?)item.ImportedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("$duplicateOf", (object?)item.DuplicateOf ?? DBNull.Value);
        command.Parameters.AddWithValue("$indexed", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}
