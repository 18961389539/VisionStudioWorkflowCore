# 警告基线（2026-10-11 复审「分析器无法加载」整改：先建立基线，再有选择升级为错误）。
# 编译全部后端项目，把告警指纹（相对路径(行,列): warning CODE）与基线比对：
#   · 新增告警 ⇒ 失败：必须修复，或显式修订基线（review 时可见）；
#   · 消失的告警 ⇒ 提示清理基线；
#   · -Update 用当前结果重写基线。
# 指纹不含本地化消息文本，语言环境不影响比对。
param(
    [string]$Configuration = 'Release',
    [string]$Baseline = (Join-Path $PSScriptRoot 'warning-baseline.txt'),
    [switch]$Update
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$backend = Join-Path $root 'backend'
$projects = @(
    'tests/VisionStudio.Api.Tests/VisionStudio.Api.Tests.csproj',
    'tests/VisionStudio.Engine.Tests/VisionStudio.Engine.Tests.csproj'
)

$fingerprints = New-Object System.Collections.Generic.List[string]
foreach ($project in $projects) {
    # --no-incremental：增量编译不会重复输出告警——基线必须来自一次完整重编。
    $output = & dotnet build (Join-Path $backend $project) -c $Configuration --nologo -v q --no-incremental 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        Write-Host $output -ForegroundColor Red
        throw "Build failed for $project; the warning baseline cannot be evaluated."
    }
    foreach ($line in ($output -split "`r?`n")) {
        if ($line -match '^(?<loc>.+?\(\d+,\d+\)):\s+warning\s+(?<code>[A-Z]+\d+):') {
            $code = $Matches['code']
            $location = $Matches['loc']
            if ($location.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
                $location = $location.Substring($root.Length).TrimStart('\', '/')
            }
            $fingerprints.Add(("{0} : warning {1}" -f ($location -replace '\\', '/'), $code))
        }
    }
}

$current = $fingerprints | Sort-Object -Unique
if ($Update) {
    Set-Content -Path $Baseline -Value ($current -join "`n") -Encoding utf8
    Write-Host "Warning baseline updated: $($current.Count) fingerprint(s) -> $Baseline" -ForegroundColor Green
    exit 0
}

$baselineSet = @()
if (Test-Path $Baseline) {
    $baselineSet = Get-Content $Baseline | Where-Object { $_ -and -not $_.StartsWith('#') }
}

$new = @($current | Where-Object { $baselineSet -notcontains $_ })
$gone = @($baselineSet | Where-object { $current -notcontains $_ })

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

Write-Host "Warning baseline holds: $($current.Count) fingerprint(s), no new warnings." -ForegroundColor Green
exit 0
