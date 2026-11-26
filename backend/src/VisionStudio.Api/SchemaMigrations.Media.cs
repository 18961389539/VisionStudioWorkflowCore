namespace VisionStudio.Api;

public sealed partial class SqliteSchemaMigrationRunner
{
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
