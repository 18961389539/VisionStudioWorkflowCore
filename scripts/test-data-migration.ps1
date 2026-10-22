# F07 regression guard: legacy data migration is staged + verified + atomically swapped.
# Dot-sources the REAL migration logic shipped by publish-runtime.ps1 (scripts/lib/DataMigration.ps1).
# ASCII-only (PS 5.1 crashes on BOM-less UTF-8 with non-ASCII).
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib/DataMigration.ps1')
$failures = 0
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        Write-Host "FAIL: $Message" -ForegroundColor Red
        $script:failures++
    }
    else {
        Write-Host "ok: $Message" -ForegroundColor Green
    }
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) "vs-migration-test-$(Get-Date -Format yyyyMMddHHmmssfff)"
New-Item $work -ItemType Directory -Force | Out-Null

function New-SourceData([string]$path) {
    New-Item $path -ItemType Directory -Force | Out-Null
    Set-Content -Path (Join-Path $path 'recipes.json') -Value '["recipe-A"]'
    New-Item (Join-Path $path 'media') -ItemType Directory -Force | Out-Null
    Set-Content -Path (Join-Path $path 'media/part1.jpg') -Value 'img'
    Set-Content -Path (Join-Path $path 'visionstudio.db') -Value 'fake-db'
}

try {
    # 1) No source -> NoSource, target untouched/not created.
    $target1 = Join-Path $work 'target1'
    $r1 = Invoke-LegacyDataMigration -SourceDataRoot (Join-Path $work 'no-source') -TargetDataRoot $target1
    Assert-True ($r1 -eq 'NoSource') "missing source returns NoSource"
    Assert-True (-not (Test-Path $target1)) "missing source does not create the target"

    # 2) Happy path: staged + verified + atomically moved, marker written, no staging residue.
    $source2 = Join-Path $work 'source2'
    New-SourceData $source2
    $target2 = Join-Path $work 'target2'
    New-Item $target2 -ItemType Directory -Force | Out-Null   # empty target, as publish-runtime creates it
    $r2 = Invoke-LegacyDataMigration -SourceDataRoot $source2 -TargetDataRoot $target2
    Assert-True ($r2 -eq 'Migrated') "source data migrates into an empty target"
    Assert-True (Test-Path (Join-Path $target2 'recipes.json')) "nested data copied (recipes)"
    Assert-True (Test-Path (Join-Path $target2 'media/part1.jpg')) "nested data copied (media)"
    $marker2 = Get-Content (Join-Path $target2 '.legacy-migration.json') -Raw | ConvertFrom-Json
    Assert-True ($marker2.items -ge 3) "completion marker records the item count"
    Assert-True ($marker2.source -eq $source2) "completion marker records the source"
    $stagingLeft = Get-ChildItem -Path $work -Directory -Filter 'target2.migrating-*' -ErrorAction SilentlyContinue
    Assert-True (-not $stagingLeft) "no staging residue after a successful migration"

    # 3) Interrupted run (injected fault before commit) -> target never sees half data; retry succeeds.
    $source3 = Join-Path $work 'source3'
    New-SourceData $source3
    $target3 = Join-Path $work 'target3'
    New-Item $target3 -ItemType Directory -Force | Out-Null
    $interrupted = $false
    try {
        Invoke-LegacyDataMigration -SourceDataRoot $source3 -TargetDataRoot $target3 -FaultInjector { throw 'simulated interruption before commit' } | Out-Null
    }
    catch { $interrupted = $true }
    Assert-True $interrupted "injected interruption surfaces an error"
    $leftovers3 = Get-ChildItem -Path $target3 -Force -ErrorAction SilentlyContinue
    Assert-True (-not $leftovers3) "interrupted migration leaves the target empty (no half data at the final path)"
    $r3 = Invoke-LegacyDataMigration -SourceDataRoot $source3 -TargetDataRoot $target3
    Assert-True ($r3 -eq 'Migrated') "retry after interruption succeeds"
    Assert-True (Test-Path (Join-Path $target3 'visionstudio.db')) "retry produced the full data set"
    $stagingLeft3 = Get-ChildItem -Path $work -Directory -Filter 'target3.migrating-*' -ErrorAction SilentlyContinue
    Assert-True (-not $stagingLeft3) "retry replaced the leftover staging directory"

    # 4) Target with foreign data (no marker) is never overwritten.
    $target4 = Join-Path $work 'target4'
    New-Item $target4 -ItemType Directory -Force | Out-Null
    Set-Content -Path (Join-Path $target4 'user-data.txt') -Value 'keep-me'
    $r4 = Invoke-LegacyDataMigration -SourceDataRoot $source2 -TargetDataRoot $target4
    Assert-True ($r4 -eq 'TargetHasData') "existing foreign data is not overwritten"
    Assert-True ((Get-Content (Join-Path $target4 'user-data.txt') -Raw).Trim() -eq 'keep-me') "foreign data intact"
    Assert-True (-not (Test-Path (Join-Path $target4 'recipes.json'))) "nothing was copied over existing data"

    # 5) A previously migrated target is recognized via the completion marker.
    $r5 = Invoke-LegacyDataMigration -SourceDataRoot $source2 -TargetDataRoot $target2
    Assert-True ($r5 -eq 'AlreadyMigrated') "own past migration is recognized"

    # 6) A non-empty WAL blocks the migration (source database not cleanly shut down).
    $source6 = Join-Path $work 'source6'
    New-SourceData $source6
    Set-Content -Path (Join-Path $source6 'visionstudio.db-wal') -Value 'pending-transactions'
    $target6 = Join-Path $work 'target6'
    $blocked = $false
    try { Invoke-LegacyDataMigration -SourceDataRoot $source6 -TargetDataRoot $target6 | Out-Null }
    catch { $blocked = $true }
    Assert-True $blocked "non-empty WAL blocks the migration"
    Assert-True (-not (Test-Path $target6)) "blocked migration does not create the target"
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures -gt 0) {
    Write-Host "DATA MIGRATION: $failures failure(s)" -ForegroundColor Red
    exit 1
}
Write-Host "DATA MIGRATION PASSED" -ForegroundColor Green
