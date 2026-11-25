# 警告基线（2026-10-11 复审「分析器无法加载」整改：先建立基线，再有选择升级为错误）。
# 编译全部后端解决方案，把告警指纹（相对路径 : warning CODE）与基线按多重集比对：
#   · 新增告警或同一文件内同错误码数量增加 ⇒ 失败：必须修复，或显式修订基线（review 时可见）；
#   · 消失的告警 ⇒ 提示清理基线；
#   · -Update 保留文件头部 # 注释并用当前结果重写基线。
# 指纹不含行列号与本地化消息文本，重构调整行号或不同语言环境不影响比对。
param(
    [string]$Configuration = 'Release',
    [string]$Baseline = (Join-Path $PSScriptRoot 'warning-baseline.txt'),
    [switch]$Update
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'backend/VisionStudio.slnx'

function Normalize-WarningFingerprint([string]$raw) {
    $trimmed = $raw.Trim()
    if ($trimmed -match '^(?<loc>.+?)(?:\(\d+,\d+\))?\s*:\s*warning\s+(?<code>[A-Z]+\d+)\b') {
        $location = $Matches['loc'].Trim()
        $code = $Matches['code'].Trim()
        $location = ($location -replace '\\', '/').TrimStart('./')
        return ("{0} : warning {1}" -f $location, $code)
    }
    return $null
}

# --no-incremental：增量编译不会重复输出告警——基线必须来自一次完整重编。
$output = & dotnet build $solution -c $Configuration --nologo -v q --no-incremental 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Write-Host $output -ForegroundColor Red
    throw "Build failed for $solution; the warning baseline cannot be evaluated."
}

$seenDiagnostics = New-Object 'System.Collections.Generic.HashSet[string]'
$fingerprints = New-Object System.Collections.Generic.List[string]
foreach ($line in ($output -split "`r?`n")) {
    if ($line -match '^(?<loc>.+?\(\d+,\d+\)):\s+warning\s+(?<code>[A-Z]+\d+):') {
        $code = $Matches['code']
        $location = $Matches['loc']
        if ($location.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
            $location = $location.Substring($root.Length).TrimStart('\', '/')
        }
        $exactKey = ("{0} : warning {1}" -f ($location -replace '\\', '/'), $code)
        # 同一源码行在多目标/被依赖项目重编时只计一次；同文件不同行的同错误码保留各自计数。
        if ($seenDiagnostics.Add($exactKey)) {
            $normalized = Normalize-WarningFingerprint $exactKey
            if ($normalized) { $fingerprints.Add($normalized) }
        }
    }
}

$current = @($fingerprints | Sort-Object)
if ($Update) {
    $headerComments = @()
    if (Test-Path $Baseline) {
        foreach ($line in (Get-Content $Baseline -Encoding UTF8)) {
            if ($line.StartsWith('#') -or [string]::IsNullOrWhiteSpace($line)) {
                $headerComments += $line
            } else {
                break
            }
        }
    }
    $content = @($headerComments) + @($current)
    [System.IO.File]::WriteAllLines($Baseline, $content, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Warning baseline updated: $($current.Count) entry(ies) -> $Baseline" -ForegroundColor Green
    exit 0
}

$baselineList = New-Object System.Collections.Generic.List[string]
if (Test-Path $Baseline) {
    # 基线是无 BOM 的 UTF-8（-Update 也按 UTF8 写出）；PS 5.1 的默认 ANSI 读取会把
    # 中文注释与首条告警解码粘连，导致已有告警被误判为新增。
    foreach ($line in (Get-Content $Baseline -Encoding UTF8)) {
        if (-not $line -or $line.StartsWith('#')) { continue }
        $norm = Normalize-WarningFingerprint $line
        if ($norm) { $baselineList.Add($norm) }
    }
}

$currentCounts = @{}
foreach ($item in $current) { $currentCounts[$item] = 1 + ($currentCounts[$item] -as [int]) }
$baselineCounts = @{}
foreach ($item in $baselineList) { $baselineCounts[$item] = 1 + ($baselineCounts[$item] -as [int]) }

$new = New-Object System.Collections.Generic.List[string]
$gone = New-Object System.Collections.Generic.List[string]
foreach ($key in ($currentCounts.Keys + $baselineCounts.Keys | Sort-Object -Unique)) {
    $cur = [int]($currentCounts[$key])
    $base = [int]($baselineCounts[$key])
    if ($cur -gt $base) {
        for ($i = 0; $i -lt ($cur - $base); $i++) { $new.Add($key) }
    } elseif ($base -gt $cur) {
        for ($i = 0; $i -lt ($base - $cur); $i++) { $gone.Add($key) }
    }
}

if ($gone.Count -gt 0) {
    Write-Host "Resolved warnings (consider pruning the baseline):" -ForegroundColor Yellow
    $gone | ForEach-Object { Write-Host "  - $_" }
}
if ($new.Count -gt 0) {
    Write-Host "NEW warnings not in the baseline:" -ForegroundColor Red
    $new | ForEach-Object { Write-Host "  + $_" }
    Write-Host "Fix them, or revise '$Baseline' explicitly (reviewable change)." -ForegroundColor Red
    exit 1
}

Write-Host "Warning baseline holds: $($current.Count) warning(s), no new warnings." -ForegroundColor Green
exit 0
