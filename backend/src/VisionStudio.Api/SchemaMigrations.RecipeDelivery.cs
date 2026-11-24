namespace VisionStudio.Api;

public sealed partial class SqliteSchemaMigrationRunner
{
    private const string RecipeDeliverySchemaSql = """
CREATE TABLE IF NOT EXISTS recipe_delivery_receipts (
    id TEXT PRIMARY KEY,
    station_id TEXT NOT NULL,
    job_id TEXT NOT NULL,
    source_version INTEGER NOT NULL,
    workflow_hash TEXT NOT NULL,
    local_version INTEGER NOT NULL,
    requires_validation INTEGER NOT NULL,
    received_at TEXT NOT NULL,
    UNIQUE (station_id, job_id, source_version, workflow_hash, requires_validation),
    UNIQUE (job_id, local_version),
    FOREIGN KEY (job_id, local_version) REFERENCES job_versions(job_id, version)
);
CREATE INDEX IF NOT EXISTS ix_recipe_delivery_received_at ON recipe_delivery_receipts(received_at DESC);

CREATE TABLE IF NOT EXISTS recipe_rollout_batches (
    id TEXT PRIMARY KEY,
    job_id TEXT NOT NULL,
    job_name TEXT NOT NULL,
    source_version INTEGER NOT NULL,
    workflow_hash TEXT NOT NULL,
    created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_recipe_rollout_created_at ON recipe_rollout_batches(created_at DESC);

CREATE TABLE IF NOT EXISTS recipe_rollout_stations (
    batch_id TEXT NOT NULL REFERENCES recipe_rollout_batches(id),
    station_id TEXT NOT NULL,
    station_name TEXT NOT NULL,
    outcome TEXT NOT NULL,
    local_version INTEGER NULL,
    detail TEXT NULL,
    PRIMARY KEY (batch_id, station_id)
);
""";

    private const string ProductionWorkstationLookupSchemaSql = """
CREATE TABLE IF NOT EXISTS recipe_rollout_latest_station (
    station_id TEXT PRIMARY KEY,
    batch_id TEXT NOT NULL,
    FOREIGN KEY (batch_id, station_id) REFERENCES recipe_rollout_stations(batch_id, station_id)
);

INSERT INTO recipe_rollout_latest_station(station_id, batch_id)
SELECT station_id, batch_id
FROM (
    SELECT s.station_id, s.batch_id,
           ROW_NUMBER() OVER (PARTITION BY s.station_id ORDER BY b.created_at DESC, b.id DESC) AS position
    FROM recipe_rollout_stations s JOIN recipe_rollout_batches b ON b.id = s.batch_id
) latest
WHERE position = 1
ON CONFLICT(station_id) DO NOTHING;

CREATE INDEX IF NOT EXISTS ix_run_traces_operator_ng_preview
    ON run_traces(job_id, job_version, started_at DESC, run_id DESC)
    WHERE source='ProductionRuntime' AND has_preview=1 AND disposition='NG' COLLATE NOCASE;
""";
}
