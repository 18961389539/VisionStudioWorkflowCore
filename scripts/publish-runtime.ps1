param([string]$Output = "", [string]$DataRoot = "")
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# F07: staged + verified + atomically-swapped legacy data migration, shared with the migration
# regression tests (scripts/test-data-migration.ps1 dot-sources the same file).
. (Join-Path $PSScriptRoot 'lib/DataMigration.ps1')

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
    npm install --no-audit --no-fund
    Assert-LastExitCode 'npm install'

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
