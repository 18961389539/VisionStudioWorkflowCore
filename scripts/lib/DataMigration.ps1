# F07: shared legacy-data migration logic for publish-runtime.ps1 and the migration regression
# tests. The tests dot-source THIS file, so the logic under test is the logic that ships.
# ASCII-only (PS 5.1 crashes on BOM-less UTF-8 with non-ASCII).
#
# Contract: the final data root only ever receives a fully copied, verified and marked directory
# via an atomic same-volume rename. Interrupted runs leave residue only in a ".migrating-*"
# staging directory (never at the final path) which is replaced on retry.

$script:VsMigrationMarkerName = '.legacy-migration.json'

function Invoke-LegacyDataMigration {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$SourceDataRoot,
        [Parameter(Mandatory = $true)][string]$TargetDataRoot,
        # Test hook: invoked after staging is complete and marked, before the atomic move.
        # Production callers pass nothing (null = no injection).
        [scriptblock]$FaultInjector = $null
    )

    if (-not (Test-Path -LiteralPath $SourceDataRoot)) { return 'NoSource' }
    $sourceEntries = Get-ChildItem -Path $SourceDataRoot -Force -ErrorAction SilentlyContinue
    if (-not $sourceEntries) { return 'NoSource' }

    # Refuse to copy a database that was not cleanly shut down: a non-empty WAL/journal means the
    # source may hold un-checkpointed transactions (stop the service, let it close, and retry).
    foreach ($pattern in @('*.db-wal', '*.db-journal')) {
        $hot = Get-ChildItem -Path $SourceDataRoot -Recurse -Force -Filter $pattern -ErrorAction SilentlyContinue |
            Where-Object { $_.Length -gt 0 } | Select-Object -First 1
        if ($hot) {
            throw "Legacy data contains a non-empty journal file '$($hot.FullName)'; the source database may not be cleanly shut down. Stop the service and retry."
        }
    }

    if (Test-Path -LiteralPath $TargetDataRoot) {
        $targetEntries = Get-ChildItem -Path $TargetDataRoot -Force -ErrorAction SilentlyContinue
        if ($targetEntries) {
            # Existing data is never overwritten; a completion marker identifies our own past run.
            if (Test-Path -LiteralPath (Join-Path $TargetDataRoot $script:VsMigrationMarkerName)) { return 'AlreadyMigrated' }
            return 'TargetHasData'
        }
    }

    # Staging lives next to the target (same volume) so the final hand-off is an atomic rename.
    $staging = "$TargetDataRoot.migrating-$([System.Guid]::NewGuid().ToString('N'))"
    $parent = Split-Path -Parent $TargetDataRoot
    $stagingPattern = "$(Split-Path -Leaf $TargetDataRoot).migrating-*"
    Get-ChildItem -Path $parent -Directory -Filter $stagingPattern -Force -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }

    New-Item $staging -ItemType Directory -Force | Out-Null
    Get-ChildItem -Path $SourceDataRoot -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $staging -Recurse -Force
    }

    # Verification: file count and byte total must match before anything moves.
    $sourceFiles = Get-ChildItem -Path $SourceDataRoot -Recurse -Force -File
    $stagingFiles = Get-ChildItem -Path $staging -Recurse -Force -File
    $sourceCount = @($sourceFiles).Count
    $sourceBytes = [long]($sourceFiles | Measure-Object -Property Length -Sum).Sum
    $stagingCount = @($stagingFiles).Count
    $stagingBytes = [long]($stagingFiles | Measure-Object -Property Length -Sum).Sum
    if ($sourceCount -ne $stagingCount -or $sourceBytes -ne $stagingBytes) {
        throw "Staged migration verification failed: source has $sourceCount file(s)/$sourceBytes byte(s), staging has $stagingCount/$stagingBytes."
    }

    # Completion marker: written after the copy, present before the move.
    $marker = [ordered]@{
        source      = $SourceDataRoot
        items       = $stagingCount
        bytes       = $stagingBytes
        completedAt = (Get-Date).ToString('o')
    } | ConvertTo-Json -Compress
    Set-Content -Path (Join-Path $staging $script:VsMigrationMarkerName) -Value $marker -Encoding UTF8

    # Test hook: fail after staging is complete but before the atomic move.
    if ($FaultInjector) { & $FaultInjector $staging }

    # Atomic hand-off: only an empty (or absent) target is replaced; anything else is a conflict.
    if (Test-Path -LiteralPath $TargetDataRoot) {
        $remaining = Get-ChildItem -Path $TargetDataRoot -Force -ErrorAction SilentlyContinue
        if ($remaining) {
            throw "Data root '$TargetDataRoot' became non-empty during migration; refusing to overwrite."
        }
        Remove-Item -LiteralPath $TargetDataRoot -Force
    }
    Move-Item -LiteralPath $staging -Destination $TargetDataRoot
    return 'Migrated'
}
