param([string]$Output = "", [string]$DataRoot = "")
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# F07: staged + verified + atomically-swapped legacy data migration, shared with the migration
# regression tests (scripts/test-data-migration.ps1 dot-sources the same file).
. (Join-Path $PSScriptRoot 'lib/DataMigration.ps1')

# R08 smoke test spawns the staged host via Start-Process: normalize case-duplicate
# environment entries first (a Git Bash/msys parent would otherwise break the spawn).
. (Join-Path $PSScriptRoot 'lib/ProcessEnvironment.ps1')
Remove-DuplicateProcessEnvironment

# Native commands (dotnet / npm) do not fail the script through ErrorActionPreference:
# every step must check its exit code explicitly and abort with a non-zero code.
function Assert-LastExitCode([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Runtime publish step failed: $step (exit code $LASTEXITCODE)." -ForegroundColor Red
        exit 1
    }
}

# Safe output boundary: publishing creates and replaces directories, so only allow targets inside
# <repo>/artifacts/. This prevents a mistyped -Output from deleting an unrelated directory.
if (-not $Output) { $Output = Join-Path $root 'artifacts/runtime-publish' }
$fullOutput = [System.IO.Path]::GetFullPath($Output)
$allowedRoot = [System.IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
if (-not $fullOutput.StartsWith($allowedRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "Output must be a directory inside '$allowedRoot' (got '$fullOutput')." -ForegroundColor Red
    exit 1
}

# R08: 生产数据必须与版本包分离。默认数据根位于发布目录之外；不得依赖"新包内新建 data/"，
# 否则升级切换后新包会创建另一套空库，配方/设备/用户/追溯看起来全部消失。
if (-not $DataRoot) { $DataRoot = Join-Path $root 'artifacts/runtime-data' }
$fullDataRoot = [System.IO.Path]::GetFullPath($DataRoot)
if ($fullDataRoot.StartsWith($fullOutput + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase) -or $fullDataRoot -eq $fullOutput) {
    Write-Host "DataRoot must live outside the version package '$fullOutput' (got '$fullDataRoot')." -ForegroundColor Red
    exit 1
}
New-Item $fullDataRoot -ItemType Directory -Force | Out-Null

$catalogPath = Join-Path $root 'frontend/src/generated/catalog.generated.json'
$distPath = Join-Path $root 'frontend/dist'
$wwwroot = Join-Path $root 'backend/src/VisionStudio.Api/wwwroot'
$staging = "$fullOutput.staging-$(Get-Date -Format yyyyMMddHHmmss)"
$previous = "$fullOutput.previous"


# 1) Preflight: restore + generated-catalog staleness. Nothing is deleted before this passes.
Push-Location "$root/backend"
try {
    dotnet restore VisionStudio.slnx
    Assert-LastExitCode 'dotnet restore'

    dotnet run --project src/VisionStudio.CatalogExporter/VisionStudio.CatalogExporter.csproj --no-restore -- --check $catalogPath
    Assert-LastExitCode 'generated frontend catalog staleness check'
}
finally { Pop-Location }

# 2) Frontend build, then stage dist into wwwroot (the publish input cache).
Push-Location "$root/frontend"
try {
    # 统一使用 npm ci 按 lockfile 精确安装（与 CI 一致）——npm install 会按 semver 浮动解析依赖，
    # 两条构建链可能漂移出不同的运行时组合。
    npm ci --no-audit --no-fund
    Assert-LastExitCode 'npm ci'

    npm run build
    Assert-LastExitCode 'npm run build'
}
finally { Pop-Location }
if (-not (Test-Path (Join-Path $distPath 'index.html'))) {
    Write-Host "Frontend build output '$distPath' is missing index.html." -ForegroundColor Red
    exit 1
}
if (Test-Path $wwwroot) { Remove-Item $wwwroot -Recurse -Force }
New-Item $wwwroot -ItemType Directory -Force | Out-Null
Copy-Item "$distPath/*" $wwwroot -Recurse -Force

# 3) Publish into a staging directory, verify it, then swap. A failed publish never touches the
#    current package; the previous version is kept at '<output>.previous' for rollback.
Push-Location "$root/backend"
try {
    dotnet publish src/VisionStudio.Api/VisionStudio.Api.csproj -c Release --no-restore -o $staging
    Assert-LastExitCode 'dotnet publish'
}
finally { Pop-Location }
if (-not (Test-Path (Join-Path $staging 'VisionStudio.Api.dll'))) {
    Write-Host "Staging publish '$staging' is missing VisionStudio.Api.dll; keeping the current package." -ForegroundColor Red
    exit 1
}

# R08 (2026-10-12 review): smoke-start the staged candidate before swapping. A package that cannot
# start must never replace the current version. The candidate runs on a loopback port with an
# isolated data root (nothing touches production data); failure aborts with the old package intact.
$smokeData = Join-Path ([System.IO.Path]::GetTempPath()) ("visionstudio-smoke-" + [Guid]::NewGuid().ToString("N"))
New-Item $smokeData -ItemType Directory -Force | Out-Null
$smokePort = Get-Random -Minimum 20000 -Maximum 40000
$smokeOutLog = Join-Path $smokeData 'host.out.log'
$smokeErrLog = Join-Path $smokeData 'host.err.log'
$smokeProcess = $null
$smokeHealthy = $false
try {
    $previousDataRoot = $env:VISIONSTUDIO_DATA_ROOT
    $previousAspNetEnv = $env:ASPNETCORE_ENVIRONMENT
    try {
        $env:VISIONSTUDIO_DATA_ROOT = $smokeData
        # Development keeps the smoke run free of production-only startup guards; it still proves
        # the package boots, binds Kestrel and serves the health endpoint with the real binaries.
        $env:ASPNETCORE_ENVIRONMENT = 'Development'
        $smokeProcess = Start-Process -FilePath 'dotnet' `
            -ArgumentList @("`"$(Join-Path $staging 'VisionStudio.Api.dll')`"", '--urls', "http://127.0.0.1:$smokePort") `
            -PassThru -NoNewWindow -RedirectStandardOutput $smokeOutLog -RedirectStandardError $smokeErrLog
    }
    finally {
        $env:VISIONSTUDIO_DATA_ROOT = $previousDataRoot
        $env:ASPNETCORE_ENVIRONMENT = $previousAspNetEnv
    }

    $smokeDeadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $smokeDeadline) {
        if ($smokeProcess.HasExited) { break }
        try {
            $smokeResponse = Invoke-WebRequest -Uri "http://127.0.0.1:$smokePort/api/health" -UseBasicParsing -TimeoutSec 3
            if ($smokeResponse.StatusCode -eq 200) { $smokeHealthy = $true; break }
        }
        catch { Start-Sleep -Milliseconds 500 }
    }
}
finally {
    if ($smokeProcess -and -not $smokeProcess.HasExited) {
        Stop-Process -Id $smokeProcess.Id -Force
        $smokeProcess.WaitForExit(10000) | Out-Null
    }
}
if (-not $smokeHealthy) {
    Write-Host "Candidate package smoke test FAILED: the staged host did not serve http://127.0.0.1:$smokePort/api/health within 90s. Keeping the current package." -ForegroundColor Red
    if (Test-Path $smokeErrLog) {
        Write-Host "--- staged host stderr (tail) ---"
        Get-Content $smokeErrLog -Tail 20
    }
    Remove-Item $smokeData -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    exit 1
}
Write-Host "Candidate package smoke test passed: health endpoint responded on the staged binaries."
Remove-Item $smokeData -Recurse -Force -ErrorAction SilentlyContinue

# R08: pre-upgrade database checkpoint. The new host migrates the database forward, and an older
# host refuses to start on a newer schema - rolling back the package alone is not enough. Snapshot
# the live database BEFORE the swap; stop the runtime first (an active writer can leave the copy
# inconsistent, this script warns by design and proceeds).
$liveDb = Join-Path $fullDataRoot 'visionstudio.db'
$checkpointDir = Join-Path $fullDataRoot 'upgrade-checkpoints'
if (Test-Path $liveDb) {
    $checkpoint = Join-Path $checkpointDir (Get-Date -Format yyyyMMddHHmmss)
    New-Item $checkpoint -ItemType Directory -Force | Out-Null
    Copy-Item $liveDb (Join-Path $checkpoint 'visionstudio.db') -Force
    Get-ChildItem $fullDataRoot -Filter 'visionstudio.db-*' -File | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $checkpoint $_.Name) -Force
    }
    Write-Host "Pre-upgrade database checkpoint saved to: $checkpoint" -ForegroundColor Yellow
} else {
    Write-Host "No live database at '$liveDb'; nothing to checkpoint before the upgrade."
}

# 迁移旧包数据到外部数据根（在删除任何东西之前完成）：F07 先把数据复制到暂存目录，校验文件
# 数量/字节数并写入完成标记，最后原子改名到最终数据根——中断重试只会留下可替换的暂存残留，
# 绝不会把半份数据当作有效目标。已有数据（含上次完整迁移的标记）一律跳过、不覆盖。
$migrationResult = Invoke-LegacyDataMigration -SourceDataRoot (Join-Path $fullOutput 'data') -TargetDataRoot $fullDataRoot
switch ($migrationResult) {
    'Migrated' { Write-Host "Legacy data migrated into the data root (staged, verified, atomically swapped)." -ForegroundColor Yellow }
    'AlreadyMigrated' { Write-Host "Data root already holds migrated data; legacy migration skipped." -ForegroundColor Yellow }
    'TargetHasData' { Write-Host "Data root '$fullDataRoot' already contains data; legacy migration skipped." -ForegroundColor Yellow }
}

# 4) 事务式切换：切换失败自动回滚旧目录，绝不留下"旧包已改名、新包未就位"的中间态。
$swapped = $false
if (Test-Path $previous) { Remove-Item $previous -Recurse -Force }
try {
    if (Test-Path $fullOutput) {
        Rename-Item $fullOutput $previous
        $swapped = $true
    }
    Move-Item $staging $fullOutput
}
catch {
    Write-Host "Package swap failed: $($_.Exception.Message). Rolling back to the previous package." -ForegroundColor Red
    if ($swapped -and (Test-Path $previous) -and -not (Test-Path $fullOutput)) {
        Rename-Item $previous $fullOutput
    }
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    exit 1
}

Write-Host "Runtime published to: $fullOutput"
Write-Host "Production data root (outside the package): $fullDataRoot"
Write-Host "Previous version kept at: $previous"
Write-Host "Start the runtime with: `$env:VISIONSTUDIO_DATA_ROOT='$fullDataRoot'; scripts/run-runtime.ps1"
Write-Host ""
Write-Host "Rollback procedure (R08):" -ForegroundColor Yellow
Write-Host "  1. Stop the host, then restore the previous package: '$previous' -> '$fullOutput'."
Write-Host "  2. Copy the pre-upgrade database checkpoint back over '$liveDb' BEFORE starting the old"
Write-Host "     host (a database migrated by the new version is rejected by the old schema guard)."
Write-Host "  3. Re-verify physical device state before resuming production - a data rollback NEVER"
Write-Host "     replaces device reconciliation, and unresolved device-action intents must be resolved"
Write-Host "     with recorded evidence (see the device-action safety gate)."
