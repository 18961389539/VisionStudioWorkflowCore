using Microsoft.Data.Sqlite;

namespace VisionStudio.Api;

public sealed partial class SqliteSchemaMigrationRunner
{
    private static async Task ApplyMediaProvenanceColumnsAsync(SqliteConnection connection, SqliteTransaction tx, CancellationToken ct)
    {
        // Keep the published v39 SQL/checksum intact, but make replay safe for restored or
        // partially downgraded databases that already contain some provenance columns.
        await EnsureColumnAsync(connection, tx, "media_index_items", "sha256", "TEXT NULL", ct);
        await EnsureColumnAsync(connection, tx, "media_index_items", "origin_path", "TEXT NULL", ct);
        await EnsureColumnAsync(connection, tx, "media_index_items", "source_kind", "TEXT NULL", ct);
        await EnsureColumnAsync(connection, tx, "media_index_items", "imported_at", "TEXT NULL", ct);
        await EnsureColumnAsync(connection, tx, "media_index_items", "imported_by", "TEXT NULL", ct);
        await EnsureColumnAsync(connection, tx, "media_index_items", "duplicate_of", "TEXT NULL", ct);
        await using var index = connection.CreateCommand();
        index.Transaction = tx;
        index.CommandText = "CREATE INDEX IF NOT EXISTS ix_media_index_sha256 ON media_index_items(sha256);";
        await index.ExecuteNonQueryAsync(ct);
    }

    private const string MediaIndexSchemaSql = """
CREATE TABLE IF NOT EXISTS media_index_items (
    relative_path TEXT PRIMARY KEY,
    collection_name TEXT NOT NULL,
    label TEXT NOT NULL,
    file_name TEXT NOT NULL,
    extension TEXT NOT NULL,
    bytes INTEGER NOT NULL,
    modified_at TEXT NOT NULL,
    width INTEGER NULL,
    height INTEGER NULL,
    channels INTEGER NULL,
    bit_depth INTEGER NULL,
    indexed_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_media_index_collection_label
    ON media_index_items(collection_name, label);
CREATE INDEX IF NOT EXISTS ix_media_index_modified
    ON media_index_items(modified_at DESC);
CREATE INDEX IF NOT EXISTS ix_media_index_file_name
    ON media_index_items(file_name COLLATE NOCASE);
""";

    private const string MediaProvenanceSchemaSql = """
ALTER TABLE media_index_items ADD COLUMN sha256 TEXT NULL;
ALTER TABLE media_index_items ADD COLUMN origin_path TEXT NULL;
ALTER TABLE media_index_items ADD COLUMN source_kind TEXT NULL;
ALTER TABLE media_index_items ADD COLUMN imported_at TEXT NULL;
ALTER TABLE media_index_items ADD COLUMN imported_by TEXT NULL;
ALTER TABLE media_index_items ADD COLUMN duplicate_of TEXT NULL;
CREATE INDEX IF NOT EXISTS ix_media_index_sha256 ON media_index_items(sha256);
""";
}
