# R08 regression guard: production data separated from the version package.
# Runs the same migrate + transactional-swap logic as publish-runtime.ps1 against fake package
# layouts (no dotnet/npm build), asserting two consecutive publishes never lose data.
# ASCII-only (PS 5.1 crashes on BOM-less UTF-8 with non-ASCII).
$ErrorActionPreference = 'Stop'
# F07: use the REAL migration logic shipped by publish-runtime.ps1 (staged + verified + atomic
# swap) instead of a duplicated simplified copy.
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

$work = Join-Path ([System.IO.Path]::GetTempPath()) "vs-publish-test-$(Get-Date -Format yyyyMMddHHmmssfff)"
New-Item $work -ItemType Directory -Force | Out-Null
$fullOutput = Join-Path $work 'runtime-publish'
$fullDataRoot = Join-Path $work 'runtime-data'
$previous = "$fullOutput.previous"
New-Item $fullDataRoot -ItemType Directory -Force | Out-Null

function New-FakePackage([string]$path, [string]$marker) {
    New-Item $path -ItemType Directory -Force | Out-Null
    Set-Content -Path (Join-Path $path 'VisionStudio.Api.dll') -Value "fake-dll-$marker"
}

function Invoke-PublishSwap([bool]$forceSwapFailure = $false) {
    $staging = "$fullOutput.staging-$([System.Guid]::NewGuid().ToString('N'))"
    New-FakePackage $staging 'new'
    # F07: real migration logic (staging + verification + completion marker + atomic move).
    $migrated = Invoke-LegacyDataMigration -SourceDataRoot (Join-Path $fullOutput 'data') -TargetDataRoot $fullDataRoot
    $swapped = $false
    if (Test-Path $previous) { Remove-Item $previous -Recurse -Force }
    try {
        if (Test-Path $fullOutput) { Rename-Item $fullOutput $previous; $swapped = $true }
        if ($forceSwapFailure) { throw 'simulated swap failure' }
        Move-Item $staging $fullOutput
    }
    catch {
        if ($swapped -and (Test-Path $previous) -and -not (Test-Path $fullOutput)) { Rename-Item $previous $fullOutput }
        if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
        throw
    }
    return $migrated
}

try {
    # 1) First publish of an old-style package that kept data inside the package.
    New-FakePackage $fullOutput 'v1'
    $legacyData = Join-Path $fullOutput 'data'
    New-Item $legacyData -ItemType Directory -Force | Out-Null
    Set-Content -Path (Join-Path $legacyData 'recipes.json') -Value '["recipe-A"]'
    New-Item (Join-Path $legacyData 'media') -ItemType Directory -Force | Out-Null
    Set-Content -Path (Join-Path $legacyData 'media/part1.jpg') -Value 'img'

    $migrated1 = Invoke-PublishSwap
    Assert-True ($migrated1 -eq 'Migrated') "first publish migrates in-package data to the external root"
    Assert-True (Test-Path (Join-Path $fullDataRoot 'recipes.json')) "recipe data survives outside the package"
    Assert-True (Test-Path (Join-Path $fullDataRoot 'media/part1.jpg')) "media data survives outside the package"
    Assert-True (Test-Path (Join-Path $fullDataRoot '.legacy-migration.json')) "migration completion marker written"

    # 2) Second publish (the upgrade): data root already has data, must NOT be overwritten,
    #    and the new package must not reset it.
    $migrated2 = Invoke-PublishSwap
    Assert-True ($migrated2 -ne 'Migrated') "second publish does not re-migrate over existing data"
    $recipe = Get-Content (Join-Path $fullDataRoot 'recipes.json') -Raw
    Assert-True ($recipe -match 'recipe-A') "data persists across a second publish (upgrade) - R08 core"

    # 3) Failed swap must roll back to the previous package instead of losing it.
    $before = Get-Content (Join-Path $fullOutput 'VisionStudio.Api.dll') -Raw
    $threw = $false
    try { Invoke-PublishSwap -forceSwapFailure $true } catch { $threw = $true }
    Assert-True $threw "swap failure surfaces an error"
    Assert-True (Test-Path (Join-Path $fullOutput 'VisionStudio.Api.dll')) "package directory exists after failed swap (rolled back)"
    $after = Get-Content (Join-Path $fullOutput 'VisionStudio.Api.dll') -Raw
    Assert-True ($before -eq $after) "failed swap restored the previous package (no data loss window)"
    Assert-True (Test-Path (Join-Path $fullDataRoot 'recipes.json')) "data root untouched by a failed swap"
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures -gt 0) {
    Write-Host "PUBLISH DATA SEPARATION: $failures failure(s)" -ForegroundColor Red
    exit 1
}
Write-Host "PUBLISH DATA SEPARATION PASSED" -ForegroundColor Green
