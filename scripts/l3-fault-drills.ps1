# L3 现场验收 · 故障注入与恢复演练（2026-10-11 复审整改）。
#
# 自动化部分（实验室/现场通用）：
#   A. 动作失败 → 意图保留 → 全入口阻断 → 管理员核对 → 恢复（记录"故障→安全阻断"与"核对→恢复"耗时）；
#   B. 未闭合意图的跨入口一致性（/api/run 与调试会话同被阻断）；
#   C. 重启一致性（实验室模式）：宿主重启后待核对意图仍可见，核对后恢复。
#
# 需要真实设备的部分以 MANUAL 清单输出（相机断线 / PLC 断电或超时 / 停止不返回 / 磁盘满），
# 由现场按清单执行并把"故障→安全恢复"预算记入报告——脚本不代替物理故障。
#
# 模式：默认实验室模式（自启虚拟设备宿主、免密）；-BaseUrl 指向现场实例，
# 若实例启用安全功能请提供 -Username/-Password（登录后携带会话 Cookie 调用核对端点）。
param(
    [string]$BaseUrl = '',
    [string]$Username = '',
    [string]$Password = '',
    [string]$OutputDir = ''
)

$ErrorActionPreference = 'Stop'
# Compatibility: a Git Bash (msys) parent can export the same variable under two spellings
# (HTTP_PROXY/http_proxy); Start-Process fails to build the child environment then.
. (Join-Path $PSScriptRoot 'lib/ProcessEnvironment.ps1')
Remove-DuplicateProcessEnvironment
$root = Split-Path -Parent $PSScriptRoot
$labMode = [string]::IsNullOrWhiteSpace($BaseUrl)
if (-not $OutputDir) {
    $OutputDir = Join-Path $root ("artifacts/l3-fault-drills-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$reportPath = Join-Path $OutputDir 'report.txt'
$script:session = $null
$script:hostProcess = $null
$results = New-Object System.Collections.Generic.List[string]

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

function Start-LabHost {
    param([string]$DataRoot)
    $apiProject = Join-Path $root 'backend/src/VisionStudio.Api/VisionStudio.Api.csproj'
    & dotnet build $apiProject -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'API host build failed.' }
    New-Item -ItemType Directory -Force -Path $DataRoot | Out-Null
    $env:VISIONSTUDIO_DATA_ROOT = $DataRoot
    $env:Security__Enabled = 'false'
    $env:RobotTcpSimulator__Enabled = 'false'
    # 演练会驱动 /api/run 与设备写入——运行模式（RuntimeOnly）会拒绝这些变更端点；
    # 演练属工程活动，实验室/现场维护窗口都应在非 RuntimeOnly 实例上执行。
    $env:VisionStudio__RuntimeOnly = 'false'
    $env:ASPNETCORE_URLS = 'http://127.0.0.1:5098'
    $env:ASPNETCORE_ENVIRONMENT = 'Production'
    $dll = Join-Path $root 'backend/src/VisionStudio.Api/bin/Release/net10.0/VisionStudio.Api.dll'
    $script:hostProcess = Start-Process -FilePath 'dotnet' -ArgumentList @("`"$dll`"") `
        -WorkingDirectory (Split-Path -Parent $apiProject) -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $OutputDir 'host.out.log') `
        -RedirectStandardError (Join-Path $OutputDir 'host.err.log')
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 2
        $health = Invoke-Api -Method Get -Url 'http://127.0.0.1:5098/api/health'
        if ($health.Status -eq 200) { return }
    }
    Get-Content (Join-Path $OutputDir 'host.err.log') -Tail 20 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ }
    throw 'API host did not become ready within 120 s (see host.err.log above).'
}

function Stop-LabHost {
    if ($script:hostProcess) {
        Stop-Process -Id $script:hostProcess.Id -Force -ErrorAction SilentlyContinue
        $script:hostProcess = $null
    }
}

function Add-Result([string]$name, [bool]$ok, [string]$detail) {
    $line = '{0}  {1}  {2}' -f ($(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
    $results.Add($line)
    Write-Host $line
}

# ---------- 启动 / 连接 ----------
if ($labMode) {
    $BaseUrl = 'http://127.0.0.1:5098'
    Start-LabHost -DataRoot (Join-Path $OutputDir 'data')
} else {
    if ($Username) {
        $login = Invoke-Api -Method Post -Url "$BaseUrl/api/auth/login" -Body @{ username = $Username; password = $Password }
        if ($login.Status -ne 200) { Write-Host "WARN: login failed (status $($login.Status)); administrator endpoints may return 401/403." -ForegroundColor Yellow }
    }
}

$failingWorkflow = @{
    id = 'l3-drill-failing-write'
    name = 'L3 Drill Failing Write'
    nodes = @(@{ id = 'w1'; type = 'device.writeTag'; name = 'Write';
        parameters = @{ deviceId = 'virtual-modbus-1'; tagId = 'NONEXISTENT_TAG'; valueType = 'String'; fallbackValue = 'DRILL' } })
    edges = @()
}
$debugWorkflow = @{
    id = 'l3-drill-debug'
    name = 'L3 Drill Debug'
    # 含设备动作的调试会话才经过动作授权闸门（纯计算调试不触碰硬件，按契约不受阻断）。
    nodes = @(@{ id = 'w1'; type = 'device.writeTag'; name = 'Write';
        parameters = @{ deviceId = 'virtual-modbus-1'; tagId = 'statusText'; fallbackValue = 'DRILL' } })
    edges = @()
}

# ---------- 场景 A：失败动作 → 意图 → 阻断 → 核对 → 恢复 ----------
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$run = Invoke-Api -Method Post -Url "$BaseUrl/api/run" -Body $failingWorkflow
$blockedAt = $null
$intentId = $null
$pending = Invoke-Api -Method Get -Url "$BaseUrl/api/production/device-actions/pending"
if ($pending.Status -eq 200) {
    $pendingJson = $pending.Body | ConvertFrom-Json
    if ($pendingJson.manualIntents) { $intentId = @($pendingJson.manualIntents)[0].runId }
}
$writeBlocked = Invoke-Api -Method Put -Url "$BaseUrl/api/devices/virtual-modbus-1/tags" -Body @{ values = @{ statusText = 'probe' } }
if ($writeBlocked.Status -eq 409) { $blockedAt = $sw.Elapsed.TotalMilliseconds }
Add-Result 'A1 failing action leaves an unresolved intent' `
    (($run.Status -ge 400) -and ($null -ne $intentId)) `
    "run=$($run.Status) intent=$intentId"
Add-Result 'A2 next action is blocked (409) immediately after the failure' `
    ($writeBlocked.Status -eq 409) `
    "write=$($writeBlocked.Status) fault-to-block=$([math]::Round($blockedAt, 0))ms"

$sw2 = [System.Diagnostics.Stopwatch]::StartNew()
$resolve = $null
if ($intentId) {
    $resolve = Invoke-Api -Method Post -Url "$BaseUrl/api/production/device-actions/resolve" -Body @{
        manualIntentId = $intentId
        reason = 'L3 fault drill: device state verified on site'
        evidence = 'L3 fault drill: trigger bit reset / no motion pending'
        deviceIdentityEvidence = 'L3 fault drill: nameplate and endpoint verified'
    }
}
$writeAfter = Invoke-Api -Method Put -Url "$BaseUrl/api/devices/virtual-modbus-1/tags" -Body @{ values = @{ statusText = 'probe2' } }
Add-Result 'A3 administrator reconciliation closes the intent' `
    (($resolve -ne $null) -and ($resolve.Status -ge 200) -and ($resolve.Status -lt 300)) `
    "resolve=$($resolve.Status)"
Add-Result 'A4 actions are allowed again after reconciliation' `
    (($writeAfter.Status -ge 200) -and ($writeAfter.Status -lt 300)) `
    "write=$($writeAfter.Status) resolve-to-recovery=$([math]::Round($sw2.Elapsed.TotalMilliseconds, 0))ms"

# ---------- 场景 B：未闭合意图跨入口一致性 ----------
$run2 = Invoke-Api -Method Post -Url "$BaseUrl/api/run" -Body $failingWorkflow
$intent2 = $null
$pending2 = Invoke-Api -Method Get -Url "$BaseUrl/api/production/device-actions/pending"
if ($pending2.Status -eq 200) {
    $pendingJson2 = $pending2.Body | ConvertFrom-Json
    if ($pendingJson2.manualIntents) { $intent2 = @($pendingJson2.manualIntents)[0].runId }
}
$debug = Invoke-Api -Method Post -Url "$BaseUrl/api/debug/sessions" -Body @{
    workflow = $debugWorkflow
    options = @{ mode = 'Breakpoints'; breakpoints = @('w1'); allowSideEffects = $true }
}
Add-Result 'B1 side-effect debug session is blocked while an intent is unresolved' `
    ($debug.Status -eq 409) `
    "debug=$($debug.Status)"

# ---------- 场景 C：重启一致性（实验室模式） ----------
if ($labMode -and $intent2) {
    Stop-LabHost
    Start-Sleep -Seconds 2
    Start-LabHost -DataRoot (Join-Path $OutputDir 'data')
    $pendingAfterRestart = Invoke-Api -Method Get -Url "$BaseUrl/api/production/device-actions/pending"
    $visibleAfterRestart = $false
    if ($pendingAfterRestart.Status -eq 200) {
        $visibleAfterRestart = ($pendingAfterRestart.Body -match [regex]::Escape($intent2))
    }
    Add-Result 'C1 unresolved intent survives a host restart (durable ledger)' `
        $visibleAfterRestart `
        "pending=$($pendingAfterRestart.Status) intent=$intent2"
    $resolve2 = Invoke-Api -Method Post -Url "$BaseUrl/api/production/device-actions/resolve" -Body @{
        manualIntentId = $intent2
        reason = 'L3 fault drill: post-restart reconciliation'
        evidence = 'L3 fault drill: device state verified after restart'
        deviceIdentityEvidence = 'L3 fault drill: nameplate and endpoint verified'
    }
    $writeFinal = Invoke-Api -Method Put -Url "$BaseUrl/api/devices/virtual-modbus-1/tags" -Body @{ values = @{ statusText = 'probe3' } }
    # R03 (2026-10-11 review): "not 409" also passed on 401/403/500/network errors - require 2xx.
    Add-Result 'C2 post-restart reconciliation restores actions' `
        (($resolve2.Status -ge 200) -and ($resolve2.Status -lt 300) -and ($writeFinal.Status -ge 200) -and ($writeFinal.Status -lt 300)) `
        "resolve=$($resolve2.Status) write=$($writeFinal.Status)"
} elseif ($intent2) {
    $resolve2 = Invoke-Api -Method Post -Url "$BaseUrl/api/production/device-actions/resolve" -Body @{
        manualIntentId = $intent2
        reason = 'L3 fault drill cleanup'
        evidence = 'L3 fault drill cleanup'
        deviceIdentityEvidence = 'L3 fault drill cleanup'
    }
    Write-Host "C (restart consistency): run in lab mode or restart the site host manually and re-check GET /api/production/device-actions/pending (intent $intent2)."
}

# ---------- MANUAL：需要真实设备的演练 ----------
$manual = @(
    'MANUAL-1 相机断线：运行取图流程时拔掉相机链路 → 运行应失败并保留意图/Unknown；恢复链路并核对后恢复。记录"断线→安全阻断"与"恢复→放行"耗时。',
    'MANUAL-2 PLC 断电/超时：写入过程中断电 → 写超时、意图 Unknown、下一动作 409；复电后核对设备状态再放行。禁止核对前重发写入。',
    'MANUAL-3 机器人停止不返回：下发运动后停止命令不返回 → 系统应保持"停止未确认"阻断，直到人工确认停止；记录隔离生效时间。',
    'MANUAL-4 磁盘满：写满数据盘 → 起始记录/追溯写入失败应按失败处理（不盲跑设备动作），清理后恢复；记录失败到恢复的窗口。',
    'MANUAL-5 节拍验证：用现场业务流程连续运行 >= 1 小时，将 P95/P99 与现场节拍预算对比（预算应包含取图/算法/通信/追溯提交）。'
)

# ---------- 报告 ----------
$failed = @($results | Where-Object { $_ -like 'FAIL*' })
$report = New-Object System.Collections.Generic.List[string]
$report.Add("L3 fault drill report  $((Get-Date).ToUniversalTime().ToString('o'))")
$report.Add("baseUrl=$BaseUrl  labMode=$labMode")
$report.Add('')
$results | ForEach-Object { $report.Add($_) }
$report.Add('')
$report.Add('Manual drills requiring physical devices (record recovery budget per item):')
$manual | ForEach-Object { $report.Add("  $_") }
$report.Add('')
$report.Add('NOTE: if a step returns 403 runtime_only, the instance runs in Runtime-only mode; run the drills in a')
$report.Add('      maintenance window with VisionStudio:RuntimeOnly=false, and restore it afterwards.')
$report.Add('')
if ($failed.Count -eq 0) {
    $report.Add('VERDICT: automated drills PASS. Field acceptance still requires the MANUAL items above plus 24/72 h soak evidence.')
} else {
    $report.Add("VERDICT: $($failed.Count) automated drill(s) FAILED — resolve before field acceptance.")
}
$report | Set-Content -Path $reportPath -Encoding utf8

if ($labMode) { Stop-LabHost }
Write-Host ''
Write-Host "report: $reportPath"
if ($failed.Count -gt 0) { exit 1 }
exit 0
