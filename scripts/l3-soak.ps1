# L3 field acceptance - long-run resource trend + tact sampler (2026-10-11 R03 rework).
#
# Purpose: collect stability/performance evidence per the independent review:
#   - resource trends: working set / private bytes / handles / threads / CPU / disk free / data-root size;
#   - business tact: drive /api/run on a fixed cadence, record per-run latency, emit P50/P95/P99;
#   - verdict: no unexplained exit, no sustained growth (head/tail window comparison + full CSV evidence).
#
# R03 rework (independent review 2026-10-11):
#   - success requires HTTP 2xx AND business result success != false (200 + success=false is a failure);
#   - health status / productionReady participate in the verdict (unreachable/unhealthy/unready -> REVIEW);
#   - missing process metrics or not enough post-warmup samples -> INCONCLUSIVE (never PASS);
#   - host process exit -> FAIL (any mode, not just lab mode);
#   - workload cadence is decoupled from the sampling cadence (1 s tick loop);
#   - failed responses are preserved verbatim in failures.log;
#   - exit codes: PASS=0, FAIL=1, REVIEW=2, INCONCLUSIVE=3 (gates must never accept 0 for a non-pass).
#
# Modes:
#   - lab (default): starts an API host (virtual devices, isolated data root, auth off) and drives load;
#   - field: -BaseUrl points at a running instance + -WorkflowFile with the site-approved workflow;
#     -ProcessId is optional but strongly recommended - without it the verdict is INCONCLUSIVE.
#
# Typical usage:
#   24 h: powershell -File scripts/l3-soak.ps1 -Duration 24:00:00
#   72 h: powershell -File scripts/l3-soak.ps1 -Duration 72:00:00 -OutputDir artifacts/l3-soak-72h
#   field: powershell -File scripts/l3-soak.ps1 -BaseUrl http://127.0.0.1:5080 -WorkflowFile site-job.json -ProcessId 1234 -Duration 24:00:00
param(
    [TimeSpan]$Duration = [TimeSpan]::FromMinutes(10),
    [int]$SampleIntervalSeconds = 60,
    [int]$WorkloadIntervalSeconds = 10,
    [int]$WarmupSeconds = 300,
    [string]$BaseUrl = '',
    [string]$WorkflowFile = '',
    [int]$ProcessId = 0,
    [string]$OutputDir = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$labMode = [string]::IsNullOrWhiteSpace($BaseUrl)
if (-not $OutputDir) {
    $OutputDir = Join-Path $root ("artifacts/l3-soak-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$csvPath = Join-Path $OutputDir 'samples.csv'
$summaryPath = Join-Path $OutputDir 'summary.txt'
$failuresPath = Join-Path $OutputDir 'failures.log'

Add-Type -AssemblyName System.Net.Http
$script:cookieJar = New-Object System.Net.CookieContainer
$script:httpHandler = New-Object System.Net.Http.HttpClientHandler
$script:httpHandler.CookieContainer = $script:cookieJar
$script:httpHandler.UseProxy = $false
$script:http = New-Object System.Net.Http.HttpClient($script:httpHandler)
$script:http.Timeout = [TimeSpan]::FromSeconds(120)

function Invoke-Api {
    param([string]$Method, [string]$Url, $Body = $null)
    try {
        $request = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod($Method.ToUpper())), $Url)
        if ($null -ne $Body) {
            $request.Content = New-Object System.Net.Http.StringContent(
                ($Body | ConvertTo-Json -Depth 12 -Compress), [System.Text.Encoding]::UTF8, 'application/json')
        }
        $response = $script:http.SendAsync($request).GetAwaiter().GetResult()
        return [pscustomobject]@{
            Status = [int]$response.StatusCode
            Body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        }
    } catch {
        return [pscustomobject]@{ Status = 0; Body = $_.Exception.GetBaseException().Message }
    }
}

function Add-FailureEvidence {
    param([string]$Line)
    Add-Content -Path $failuresPath -Value ("{0:O} {1}" -f (Get-Date).ToUniversalTime(), $Line) -Encoding utf8
}

# ---------- lab mode: start a virtual-device host ----------
$hostProcess = $null
if ($labMode) {
    $apiProject = Join-Path $root 'backend/src/VisionStudio.Api/VisionStudio.Api.csproj'
    Write-Host "[lab] building API host..."
    & dotnet build $apiProject -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'API host build failed.' }

    $dataRoot = Join-Path $OutputDir 'data'
    New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
    $env:VISIONSTUDIO_DATA_ROOT = $dataRoot
    $env:Security__Enabled = 'false'
    $env:RobotTcpSimulator__Enabled = 'false'
    # The workload drives /api/run - RuntimeOnly mode rejects it; lab mode turns it off explicitly.
    $env:VisionStudio__RuntimeOnly = 'false'
    $env:ASPNETCORE_URLS = 'http://127.0.0.1:5099'
    $env:ASPNETCORE_ENVIRONMENT = 'Production'
    $BaseUrl = 'http://127.0.0.1:5099'
    $dll = Join-Path $root 'backend/src/VisionStudio.Api/bin/Release/net10.0/VisionStudio.Api.dll'
    $hostProcess = Start-Process -FilePath 'dotnet' -ArgumentList @("`"$dll`"") `
        -WorkingDirectory (Split-Path -Parent $apiProject) -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $OutputDir 'host.out.log') `
        -RedirectStandardError (Join-Path $OutputDir 'host.err.log')
    $ProcessId = $hostProcess.Id
    Write-Host "[lab] host started (pid $ProcessId), waiting for readiness..."
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 2
        $health = Invoke-Api -Method Get -Url "$BaseUrl/api/health"
        if ($health.Status -eq 200) { $ready = $true; break }
    }
    if (-not $ready) {
        Get-Content (Join-Path $OutputDir 'host.err.log') -Tail 20 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ }
        throw 'API host did not become ready within 120 s (see host.err.log above).'
    }

    if (-not $WorkflowFile) {
        # Default load: virtual side-effect write (full intent -> execute -> trace -> close lifecycle).
        $workflow = @{
            id = 'l3-soak-workload'
            name = 'L3 Soak Workload'
            nodes = @(
                @{ id = 'w1'; type = 'device.writeTag'; name = 'Write';
                   parameters = @{ deviceId = 'virtual-modbus-1'; tagId = 'statusText'; fallbackValue = 'SOAK' } }
            )
            edges = @()
        }
    }
} else {
    if (-not $WorkflowFile) {
        throw 'Field mode requires -WorkflowFile (the site-approved workflow). Refusing to guess a workflow that may command real hardware.'
    }
    # Preflight: RuntimeOnly instances reject /api/run workload driving.
    $probe = Invoke-Api -Method Get -Url "$BaseUrl/api/health"
    if ($probe.Status -ne 200) {
        throw "Preflight failed: $BaseUrl/api/health returned $($probe.Status)."
    }
}

if ($WorkflowFile) {
    $workflow = Get-Content $WorkflowFile -Raw -Encoding UTF8 | ConvertFrom-Json
}

# ---------- sampling (1 s tick; sample and workload cadences are independent) ----------
$samples = New-Object System.Collections.Generic.List[object]
$runDurations = New-Object System.Collections.Generic.List[double]
$runFailures = 0
$healthFailures = 0
$soakStart = Get-Date
$deadline = $soakStart.Add($Duration)
$nextSample = $soakStart
$nextWorkload = $soakStart

'utcTime,workingSetMB,privateMB,handles,threads,cpuSeconds,diskFreeGB,dataRootMB,healthStatus,productionReady,runMs,runStatus,runSuccess' |
    Set-Content -Path $csvPath -Encoding utf8
Set-Content -Path $failuresPath -Value "failed workload/health responses are preserved verbatim below" -Encoding utf8

Write-Host "[soak] sampling for $Duration (interval ${SampleIntervalSeconds}s, workload every ${WorkloadIntervalSeconds}s)..."
while ((Get-Date) -lt $deadline) {
    $now = Get-Date

    # ----- workload driver (own cadence; resync if the loop fell behind) -----
    if ($now -ge $nextWorkload) {
        $nextWorkload = $nextWorkload.AddSeconds($WorkloadIntervalSeconds)
        if ((Get-Date) - $nextWorkload -gt [TimeSpan]::FromSeconds($WorkloadIntervalSeconds)) { $nextWorkload = Get-Date }
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $run = Invoke-Api -Method Post -Url "$BaseUrl/api/run" -Body $workflow
        $sw.Stop()
        $runMs = [math]::Round($sw.Elapsed.TotalMilliseconds, 1)
        $runStatus = [string]$run.Status
        $runSuccess = ''
        $accepted = $false
        if ($run.Status -ge 200 -and $run.Status -lt 300) {
            try {
                $body = $run.Body | ConvertFrom-Json
                $runSuccess = [string]$body.success
            } catch { $runSuccess = 'unparsed' }
            # R03: HTTP 2xx alone is not success - a business result of success=false is a failure.
            if ($runSuccess -eq 'False') {
                $runFailures++
                Add-FailureEvidence "workload HTTP $($run.Status) but success=false: $($run.Body)"
            } else {
                $accepted = $true
                # Warmup-window tact (JIT/cache cold start) is excluded from percentiles.
                if (((Get-Date) - $soakStart).TotalSeconds -ge $WarmupSeconds) { $runDurations.Add($sw.Elapsed.TotalMilliseconds) }
            }
        } else {
            $runFailures++
            Add-FailureEvidence "workload HTTP $($run.Status): $($run.Body)"
        }
        $script:lastRunMs = $runMs
        $script:lastRunStatus = $runStatus
        $script:lastRunSuccess = $runSuccess
        $script:workloadThisSample = $true
    }

    # ----- resource/health sample (own cadence) -----
    if ((Get-Date) -ge $nextSample) {
        $nextSample = (Get-Date).AddSeconds($SampleIntervalSeconds)
        $proc = $null
        if ($ProcessId -gt 0) { $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue }

        $health = Invoke-Api -Method Get -Url "$BaseUrl/api/health"
        $healthStatus = 'unreachable'
        $productionReady = ''
        if ($health.Status -eq 200) {
            try {
                $healthJson = $health.Body | ConvertFrom-Json
                $healthStatus = [string]$healthJson.status
                $productionReady = [string]$healthJson.productionReady
            } catch { $healthStatus = 'unparsed' }
        }

        $drive = $null
        try { $drive = Get-PSDrive -Name ([System.IO.Path]::GetPathRoot($OutputDir).TrimEnd('\').TrimEnd(':')) -ErrorAction Stop } catch { }
        $dataRootMB = ''
        if ($labMode -and (Test-Path (Join-Path $OutputDir 'data'))) {
            $dataRootMB = [math]::Round(((Get-ChildItem (Join-Path $OutputDir 'data') -Recurse -File -ErrorAction SilentlyContinue |
                Measure-Object Length -Sum).Sum / 1MB), 2)
        }

        $line = '{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12}' -f `
            (Get-Date).ToUniversalTime().ToString('o'), `
            $(if ($proc) { [math]::Round($proc.WorkingSet64 / 1MB, 1) } else { '' }), `
            $(if ($proc) { [math]::Round($proc.PrivateMemorySize64 / 1MB, 1) } else { '' }), `
            $(if ($proc) { $proc.HandleCount } else { '' }), `
            $(if ($proc) { $proc.Threads.Count } else { '' }), `
            $(if ($proc) { [math]::Round($proc.TotalProcessorTime.TotalSeconds, 1) } else { '' }), `
            $(if ($drive) { [math]::Round($drive.Free / 1GB, 2) } else { '' }), `
            $dataRootMB, $healthStatus, $productionReady, $script:lastRunMs, $script:lastRunStatus, $script:lastRunSuccess
        Add-Content -Path $csvPath -Value $line -Encoding utf8
        $samples.Add($line)
        $script:workloadThisSample = $false
    }

    Start-Sleep -Seconds 1
}

# ---------- summary & verdict ----------
function Get-Percentile($values, $p) {
    if ($values.Count -eq 0) { return $null }
    $sorted = @($values | Sort-Object)
    $index = [math]::Max(0, [math]::Ceiling($p / 100 * $sorted.Count) - 1)
    return [math]::Round($sorted[$index], 1)
}

$summary = New-Object System.Collections.Generic.List[string]
$summary.Add("L3 soak summary  ($((Get-Date).ToUniversalTime().ToString('o')))")
$summary.Add("duration=$Duration  samples=$($samples.Count)  warmupExcluded=${WarmupSeconds}s  baseUrl=$BaseUrl  labMode=$labMode  processId=$ProcessId")
$summary.Add('')

if ($runDurations.Count -gt 0) {
    $summary.Add("cycle time: count=$($runDurations.Count)  failures=$runFailures")
    $summary.Add("  P50=$(Get-Percentile $runDurations 50) ms  P95=$(Get-Percentile $runDurations 95) ms  P99=$(Get-Percentile $runDurations 99) ms")
} else {
    $summary.Add("cycle time: no successful workload runs (failures=$runFailures)")
}

$fails = New-Object System.Collections.Generic.List[string]
$reviews = New-Object System.Collections.Generic.List[string]
$inconclusive = New-Object System.Collections.Generic.List[string]

if ($runFailures -gt 0) {
    $reviews.Add("REVIEW: $runFailures workload run(s) failed (HTTP error or success=false) - see failures.log for the raw responses and samples.csv runStatus/runSuccess columns.")
}

# Health evidence must participate in the verdict, not just be archived (R03).
foreach ($line in $samples) {
    $cells = $line -split ','
    $hs = $cells[8]
    $pr = $cells[9]
    if ($hs -ne 'healthy') {
        $reviews.Add("REVIEW: health was '$hs' (expected 'healthy') during the run - see samples.csv healthStatus column.")
        break
    }
}
if (-not $labMode) {
    foreach ($line in $samples) {
        $cells = $line -split ','
        if ($cells[9] -ne '' -and $cells[9] -ne 'True') {
            $reviews.Add("REVIEW: productionReady=$($cells[9]) in field mode - the production runtime never became ready; see samples.csv.")
            break
        }
    }
}

# Process liveness: an unexplained exit is a hard FAIL in every mode (R03).
if ($ProcessId -gt 0 -and -not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
    $fails.Add('FAIL: the host process exited during the soak - unexplained exit is an L3 blocker.')
}

# Resource trends: head/tail 10% window averages (not just endpoints); full curve stays in the CSV.
# Warmup samples (JIT/cache/first SQLite pages) are excluded from the verdict but kept in the CSV.
$warmupSamples = [int][math]::Ceiling($WarmupSeconds / [math]::Max(1, $SampleIntervalSeconds))
$parsed = @($samples | Select-Object -Skip $warmupSamples | ForEach-Object { $cells = $_ -split ','; [pscustomobject]@{
    ws = [double]($cells[1] -replace '^\s*$', '0'); pv = [double]($cells[2] -replace '^\s*$', '0');
    handles = [double]($cells[3] -replace '^\s*$', '0'); threads = [double]($cells[4] -replace '^\s*$', '0');
    hasMetrics = ($cells[1] -match '\S') } })
if (-not ($parsed | Where-Object { -not $_.hasMetrics })) {
    # All post-warmup samples carry real process metrics.
    if ($parsed.Count -ge 10) {
        $window = [math]::Max(1, [int][math]::Floor($parsed.Count * 0.1))
        $head = $parsed | Select-Object -First $window
        $tail = $parsed | Select-Object -Last $window
        foreach ($metric in @(@('workingSetMB', 'ws'), @('privateMB', 'pv'), @('handles', 'handles'), @('threads', 'threads'))) {
            $name = $metric[0]; $key = $metric[1]
            $first = ($head | Measure-Object -Property $key -Average).Average
            $last = ($tail | Measure-Object -Property $key -Average).Average
            $growth = if ($first -gt 0) { [math]::Round(($last - $first) / $first * 100, 1) } else { 0 }
            $summary.Add(("trend {0,-12}: first-window-avg={1}  last-window-avg={2}  growth={3}%" -f $name, [math]::Round($first, 1), [math]::Round($last, 1), $growth))
            if ($growth -gt 20) {
                $reviews.Add("REVIEW: $name grew ${growth}% across the run (threshold 20%) - inspect samples.csv for the curve before accepting.")
            }
        }
    } else {
        # R03: not enough steady-state samples must never PASS - it is INCONCLUSIVE evidence.
        $inconclusive.Add("INCONCLUSIVE: not enough post-warmup samples ($($parsed.Count), need >= 10) for windowed trend comparison - run longer.")
    }
} else {
    # R03: absent process metrics must never be silently converted to zeros - evidence is incomplete.
    $inconclusive.Add('INCONCLUSIVE: process metrics are unavailable for at least one sample (field mode without -ProcessId?) - resource trend evidence is incomplete.')
}

if ($labMode) {
    if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
        Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
        $summary.Add('host: exited under script control at the end of the run.')
    }
}

$summary.Add('')
if ($fails.Count -gt 0) {
    $summary.Add('VERDICT: FAIL')
    $fails | ForEach-Object { $summary.Add("  - $_") }
    $reviews | ForEach-Object { $summary.Add("  - $_") }
} elseif ($reviews.Count -gt 0) {
    $summary.Add('VERDICT: REVIEW')
    $reviews | ForEach-Object { $summary.Add("  - $_") }
    $inconclusive | ForEach-Object { $summary.Add("  - $_") }
} elseif ($inconclusive.Count -gt 0) {
    $summary.Add('VERDICT: INCONCLUSIVE (evidence incomplete - not a pass)')
    $inconclusive | ForEach-Object { $summary.Add("  - $_") }
} else {
    $summary.Add('VERDICT: PASS (virtual-scale) - no unexplained exit, no sustained growth, workload and health consistent.')
    $summary.Add('NOTE: 24/72 h duration, real devices and on-site tact budgets remain field acceptance items.')
}

$summary | Set-Content -Path $summaryPath -Encoding utf8
$summary | ForEach-Object { Write-Host $_ }
Write-Host ""
Write-Host "evidence : $csvPath"
Write-Host "summary  : $summaryPath"
Write-Host "failures : $failuresPath"

# R03: verdicts map to distinct exit codes so gates can never mistake a non-pass for success.
if ($fails.Count -gt 0) { exit 1 }
elseif ($reviews.Count -gt 0) { exit 2 }
elseif ($inconclusive.Count -gt 0) { exit 3 }
else { exit 0 }
