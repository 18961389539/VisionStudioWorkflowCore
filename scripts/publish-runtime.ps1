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

# R08/F07 (2026-10-12 review): smoke-start the staged candidate before swapping. A package that
# cannot start must never replace the current version. The candidate runs with ITS OWN content
# root (WorkingDirectory=$staging) under the Production environment with an isolated data root and
# the approved test configuration (security and simulators off), and the check verifies more than
# an HTTP 200: required assets, health endpoint, the packaged frontend (wwwroot index) and a clean
# plugin/dependency load in the host log.
$smokeRequiredAssets = @('VisionStudio.Api.dll', 'appsettings.json', 'wwwroot/index.html', 'VisionStudio.Plugin.Worker.dll')
$missingAssets = @($smokeRequiredAssets | Where-Object { -not (Test-Path (Join-Path $staging $_)) })
if ($missingAssets.Count -gt 0) {
    Write-Host "Candidate package is missing required assets: $($missingAssets -join ', '). Keeping the current package." -ForegroundColor Red
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    exit 1
}
$smokeData = Join-Path ([System.IO.Path]::GetTempPath()) ("visionstudio-smoke-" + [Guid]::NewGuid().ToString("N"))
New-Item $smokeData -ItemType Directory -Force | Out-Null
$smokePort = Get-Random -Minimum 20000 -Maximum 40000
$smokeOutLog = Join-Path $smokeData 'host.out.log'
$smokeErrLog = Join-Path $smokeData 'host.err.log'
$smokeProcess = $null
$smokeHealthy = $false
$smokeIndexOk = $false
try {
    $previousDataRoot = $env:VISIONSTUDIO_DATA_ROOT
    $previousAspNetEnv = $env:ASPNETCORE_ENVIRONMENT
    $previousSecurity = $env:Security__Enabled
    $previousRobotSim = $env:RobotTcpSimulator__Enabled
    try {
        $env:VISIONSTUDIO_DATA_ROOT = $smokeData
        # Production environment exercises the same configuration chain as the deploy target;
        # the startup guards are satisfied through the approved test configuration (security off,
        # simulators off; AutoStart stays off because the isolated data root has no config file).
        $env:ASPNETCORE_ENVIRONMENT = 'Production'
        $env:Security__Enabled = 'false'
        $env:RobotTcpSimulator__Enabled = 'false'
        $smokeProcess = Start-Process -FilePath 'dotnet' `
            -ArgumentList @("`"$(Join-Path $staging 'VisionStudio.Api.dll')`"", '--urls', "http://127.0.0.1:$smokePort") `
            -WorkingDirectory $staging `
            -PassThru -NoNewWindow -RedirectStandardOutput $smokeOutLog -RedirectStandardError $smokeErrLog
    }
    finally {
        $env:VISIONSTUDIO_DATA_ROOT = $previousDataRoot
        $env:ASPNETCORE_ENVIRONMENT = $previousAspNetEnv
        $env:Security__Enabled = $previousSecurity
        $env:RobotTcpSimulator__Enabled = $previousRobotSim
    }

    $smokeDeadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $smokeDeadline) {
        if ($smokeProcess.HasExited) { break }
        try {
            $smokeResponse = Invoke-WebRequest -Uri "http://127.0.0.1:$smokePort/api/health" -UseBasicParsing -TimeoutSec 3
            if ($smokeResponse.StatusCode -eq 200) {
                # A06 (2026-10-12 review): HTTP 200 is not health - parse the body. The release must
                # not treat "degraded" or "not ready" as a passing candidate; blockers are recorded
                # and reported so the operator sees WHY the candidate was rejected.
                $health = [string]$smokeResponse.Content | ConvertFrom-Json
                if ($health.status -eq 'ok' -and $health.ready -eq $true) { $smokeHealthy = $true; break }
                $smokeBlockers = (@($health.degradedReasons) + @($health.productionBlockers)) -join '; '
            }
        }
        catch { Start-Sleep -Milliseconds 500 }
    }
    if ($smokeHealthy) {
        # The packaged frontend must be served by the candidate's own content root.
        try {
            $indexResponse = Invoke-WebRequest -Uri "http://127.0.0.1:$smokePort/" -UseBasicParsing -TimeoutSec 5
            $smokeIndexOk = $indexResponse.StatusCode -eq 200 -and ([string]$indexResponse.Content) -match 'id="root"'
        }
        catch { $smokeIndexOk = $false }
    }
}
finally {
    if ($smokeProcess -and -not $smokeProcess.HasExited) {
        Stop-Process -Id $smokeProcess.Id -Force
        $smokeProcess.WaitForExit(10000) | Out-Null
    }
}
# Plugin/dependency load failures (including a missing worker DLL) fail the publish too - the
# candidate package assets must be complete.
$smokePluginFailure = if (Test-Path $smokeOutLog) { [bool](Select-String -Path $smokeOutLog -Pattern 'Failed to load VisionStudio plugin package' -Quiet) } else { $false }
if (-not $smokeHealthy -or -not $smokeIndexOk -or $smokePluginFailure) {
    Write-Host "Candidate package smoke test FAILED (health=$smokeHealthy, frontend=$smokeIndexOk, pluginLoadFailure=$smokePluginFailure). Keeping the current package." -ForegroundColor Red
    if ($smokeBlockers) { Write-Host "health blockers: $smokeBlockers" -ForegroundColor Red }
    foreach ($logPath in @($smokeOutLog, $smokeErrLog)) {
        if (Test-Path $logPath) {
            Write-Host "--- staged host log (tail): $logPath ---"
            Get-Content $logPath -Tail 20
        }
    }
    Remove-Item $smokeData -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    exit 1
}
Write-Host "Candidate package smoke test passed: health (status=ok, ready=true) + packaged frontend + clean plugin load on the staged binaries."
Write-Host ("candidate VisionStudio.Api.dll sha256: {0}" -f (Get-FileHash (Join-Path $staging 'VisionStudio.Api.dll') -Algorithm SHA256).Hash.Substring(0, 16).ToLowerInvariant())
Write-Host "approved test-configuration blockers for this smoke run: isolated data root, Security disabled, simulators disabled, AutoStart off. Target-config validation (security enabled, site config, real dependencies) stays on the field acceptance checklist."
Remove-Item $smokeData -Recurse -Force -ErrorAction SilentlyContinue

# A06 stage 2 (2026-10-12 review): the candidate must also boot under the TARGET security
# posture - Security enabled, AutoLoginAdmin off, loopback bind (the Production guards reject
# anything else), fresh isolated data root. The check proves more than "the process started":
# health must be ok/ready, the packaged frontend must still be served, and a protected endpoint
# must reject anonymous access (401) - i.e. the security pipeline is actually engaged.
$secureData = Join-Path ([System.IO.Path]::GetTempPath()) ("visionstudio-secsmoke-" + [Guid]::NewGuid().ToString("N"))
New-Item $secureData -ItemType Directory -Force | Out-Null
$securePort = Get-Random -Minimum 20000 -Maximum 40000
$secureOutLog = Join-Path $secureData 'host.out.log'
$secureErrLog = Join-Path $secureData 'host.err.log'
$secureProcess = $null
$secureHealthy = $false
$secureAuthOk = $false
$secureFrontendOk = $false
try {
    $previousDataRoot = $env:VISIONSTUDIO_DATA_ROOT
    $previousAspNetEnv = $env:ASPNETCORE_ENVIRONMENT
    $previousSecurity = $env:Security__Enabled
    $previousAutoLogin = $env:Security__AutoLoginAdmin
    $previousRobotSim = $env:RobotTcpSimulator__Enabled
    try {
        $env:VISIONSTUDIO_DATA_ROOT = $secureData
        $env:ASPNETCORE_ENVIRONMENT = 'Production'
        $env:Security__Enabled = 'true'
        $env:Security__AutoLoginAdmin = 'false'
        $env:RobotTcpSimulator__Enabled = 'false'
        $secureProcess = Start-Process -FilePath 'dotnet' `
            -ArgumentList @("`"$(Join-Path $staging 'VisionStudio.Api.dll')`"", '--urls', "http://127.0.0.1:$securePort") `
            -WorkingDirectory $staging `
            -PassThru -NoNewWindow -RedirectStandardOutput $secureOutLog -RedirectStandardError $secureErrLog
    }
    finally {
        $env:VISIONSTUDIO_DATA_ROOT = $previousDataRoot
        $env:ASPNETCORE_ENVIRONMENT = $previousAspNetEnv
        $env:Security__Enabled = $previousSecurity
        $env:Security__AutoLoginAdmin = $previousAutoLogin
        $env:RobotTcpSimulator__Enabled = $previousRobotSim
    }

    $secureDeadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $secureDeadline) {
        if ($secureProcess.HasExited) { break }
        try {
            $secureHealthResponse = Invoke-WebRequest -Uri "http://127.0.0.1:$securePort/api/health" -UseBasicParsing -TimeoutSec 3
            if ($secureHealthResponse.StatusCode -eq 200) {
                $secureHealth = [string]$secureHealthResponse.Content | ConvertFrom-Json
                if ($secureHealth.status -eq 'ok' -and $secureHealth.ready -eq $true) { $secureHealthy = $true; break }
            }
        }
        catch { Start-Sleep -Milliseconds 500 }
    }
    if ($secureHealthy) {
        try {
            $secureIndex = Invoke-WebRequest -Uri "http://127.0.0.1:$securePort/" -UseBasicParsing -TimeoutSec 5
            $secureFrontendOk = $secureIndex.StatusCode -eq 200 -and ([string]$secureIndex.Content) -match 'id="root"'
        }
        catch { $secureFrontendOk = $false }
        # 受保护端点必须拒绝匿名访问（PowerShell 5.1 对 401 抛异常，从响应取状态码）。
        try {
            $null = Invoke-WebRequest -Uri "http://127.0.0.1:$securePort/api/modules" -UseBasicParsing -TimeoutSec 5
        }
        catch {
            if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 401) { $secureAuthOk = $true }
        }
    }
}
finally {
    if ($secureProcess -and -not $secureProcess.HasExited) {
        Stop-Process -Id $secureProcess.Id -Force
        $secureProcess.WaitForExit(10000) | Out-Null
    }
}
if (-not $secureHealthy -or -not $secureFrontendOk -or -not $secureAuthOk) {
    Write-Host "Secure-configuration smoke stage FAILED (health=$secureHealthy, frontend=$secureFrontendOk, anonymous-401=$secureAuthOk). Keeping the current package." -ForegroundColor Red
    foreach ($logPath in @($secureOutLog, $secureErrLog)) {
        if (Test-Path $logPath) {
            Write-Host "--- secure-stage host log (tail): $logPath ---"
            Get-Content $logPath -Tail 20
        }
    }
    Remove-Item $secureData -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    exit 1
}
Write-Host "Secure-configuration smoke stage passed: health ok/ready with Security enabled + anonymous access rejected (401) + packaged frontend served."
Remove-Item $secureData -Recurse -Force -ErrorAction SilentlyContinue

# R08/F06 (2026-10-12 review): pre-upgrade database checkpoint via the SQLite Online Backup API
# (consistent under an active writer/checkpoint - the old sequential file copy could tear), with
# integrity + schema recorded in a manifest. The new host migrates the database forward and an
# older host refuses a newer schema, so rolling back the package alone is not enough; restore the
# checkpoint with the maintenance tool in a stop-the-world window (see the rollback notes below).
$liveDb = Join-Path $fullDataRoot 'visionstudio.db'
$checkpointDir = Join-Path $fullDataRoot 'upgrade-checkpoints'
if (Test-Path $liveDb) {
    $checkpoint = Join-Path $checkpointDir (Get-Date -Format yyyyMMddHHmmss)
    New-Item $checkpoint -ItemType Directory -Force | Out-Null
    dotnet run --project "$root/backend/src/VisionStudio.SqliteSnapshot/VisionStudio.SqliteSnapshot.csproj" -c Release -- snapshot $liveDb $checkpoint
    Assert-LastExitCode 'pre-upgrade database checkpoint (online backup API)'
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
Write-Host "Rollback procedure (R08/F06):" -ForegroundColor Yellow
Write-Host "  1. Stop the host, then restore the previous package: '$previous' -> '$fullOutput'."
Write-Host "  2. Restore the pre-upgrade database from its checkpoint with the maintenance tool"
Write-Host "     (the tool verifies the checkpoint, cleans target WAL/SHM sidecars and replaces the"
Write-Host "     database as one unit):"
Write-Host "       dotnet run --project backend/src/VisionStudio.SqliteSnapshot -c Release -- restore <checkpointDir> '$liveDb'"
Write-Host "     (a database migrated by the new version is rejected by the old schema guard, so the"
Write-Host "     package rollback alone is not enough)."
Write-Host "  3. Re-verify physical device state before resuming production - a data rollback NEVER"
Write-Host "     replaces device reconciliation, and unresolved device-action intents must be resolved"
Write-Host "     with recorded evidence (see the device-action safety gate)."
