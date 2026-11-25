<#
.SYNOPSIS
    VisionStudio 质量门禁。

.DESCRIPTION
    2026-11-12：增加 -Scope，让 CI 把一条 25 分钟的串行流水线拆成可并行的三段：

      Backend  静态审计 + 度量下限 + 发布/迁移回归 + dotnet test + 告警基线 + 覆盖率
      Frontend npm ci + lint + 单测与覆盖率 + 生产构建 + 产物体积预算
      E2E      Playwright 真浏览器回归
      All（默认，本地用）以上全部，按原顺序执行

    -SkipInstall：仅用于本地反复跑门禁时复用已有 node_modules。CI 必须保持默认（不加此开关）。
#>
param(
    [ValidateSet('All', 'Backend', 'Frontend', 'E2E')]
    [string]$Scope = 'All',

    # 跳过 npm ci，复用现有 node_modules（本地迭代用；CI 不要加）。
    [switch]$SkipInstall
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$results = Join-Path $root 'artifacts/test-results'
$backendCoverage = Join-Path $results 'backend-coverage'
$runBackend = $Scope -in @('All', 'Backend')
$runFrontend = $Scope -in @('All', 'Frontend')
$runE2E = $Scope -in @('All', 'E2E')

. (Join-Path $PSScriptRoot 'lib/QualityGateHelpers.ps1')

$minCoverage = if ($env:MIN_LINE_COVERAGE) { $env:MIN_LINE_COVERAGE } else { '84' }
$minBranchCoverage = if ($env:MIN_BRANCH_COVERAGE) { $env:MIN_BRANCH_COVERAGE } else { '62' }

function Assert-LastExitCode([string]$step) {
    Assert-StepExitCode $step
}

function Normalize-EnvironmentForStartProcess {
    if ($PSVersionTable.PSVersion.Major -ge 6) { return }
    $snapshot = [System.Environment]::GetEnvironmentVariables()
    $groups = @{}
    foreach ($key in @($snapshot.Keys)) {
        $name = [string]$key
        $canonical = $name.ToUpperInvariant()
        if (-not $groups.ContainsKey($canonical)) {
            $groups[$canonical] = New-Object System.Collections.ArrayList
        }
        [void]$groups[$canonical].Add($name)
    }

    $normalized = 0
    foreach ($canonical in @($groups.Keys)) {
        $variants = $groups[$canonical]
        if ($variants.Count -lt 2) { continue }
        $value = $snapshot[$canonical]
        if ($null -eq $value) { $value = $snapshot[$variants[0]] }
        foreach ($variant in $variants) {
            [System.Environment]::SetEnvironmentVariable($variant, $null, 'Process')
        }
        if ($null -ne $value) {
            [System.Environment]::SetEnvironmentVariable($canonical, [string]$value, 'Process')
        }
        $normalized++
    }
    if ($normalized -gt 0) {
        Write-Host "E2E: normalized $normalized duplicate environment variable group(s) for Start-Process."
    }

    $after = @([System.Environment]::GetEnvironmentVariables().Keys |
        ForEach-Object { ([string]$_).ToUpperInvariant() })
    if (@($after | Group-Object | Where-Object { $_.Count -gt 1 }).Count -gt 0) {
        throw 'Environment still contains case-insensitive duplicate variable names; Start-Process cannot inherit it.'
    }
}

function Clear-ChildDirectories([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        foreach ($child in @(Get-ChildItem -LiteralPath $Path -Directory -ErrorAction SilentlyContinue)) {
            Remove-Item -LiteralPath $child.FullName -Recurse -Force -ErrorAction Continue
        }
    } finally {
        $ErrorActionPreference = $previousPreference
    }

    $remaining = @(Get-ChildItem -LiteralPath $Path -Directory -ErrorAction SilentlyContinue)
    if ($remaining.Count -gt 0) {
        Write-Host "Failed to clear stale coverage directories under: $Path ($($remaining.Count) left)" -ForegroundColor Red
        exit 1
    }
}

$python = $null
if ($runBackend -or $runFrontend) {
    $python = Resolve-PythonCommand
    if ($null -eq $python) {
        Write-Host 'Python interpreter not found (py / python / python3). Backend and Frontend scopes require it for audits and budgets.' -ForegroundColor Red
        exit 1
    }
}

if ($runBackend) {
    New-Item $results -ItemType Directory -Force | Out-Null
    New-Item $backendCoverage -ItemType Directory -Force | Out-Null
    Clear-ChildDirectories $backendCoverage
}

Write-Host "== VisionStudio V0.63 Quality Gate (scope=$Scope) =="
if (-not $runBackend) {
    Write-Host 'Skipping repository-wide checks (static audit / metric floor / publish regressions): scope is not Backend.'
}
if ($runBackend) {
    & $python "$root/scripts/static-audit.py"
    Assert-LastExitCode 'static audit'

    & $python "$root/scripts/check-metric-floor.py"
    Assert-LastExitCode 'metric floor'

    & $python "$root/scripts/check-size-budget.py"
    Assert-LastExitCode 'size budget'

    & "$root/scripts/test-publish-data-separation.ps1"
    Assert-LastExitCode 'publish data separation'

    & "$root/scripts/test-data-migration.ps1"
    Assert-LastExitCode 'data migration staging'

    $bash = Get-Command bash -ErrorAction SilentlyContinue
    if ($bash) {
        & bash "$root/scripts/test-publish-runtime.sh"
        Assert-LastExitCode 'bash publish regression'
    } else {
        Write-Host 'Skipping bash publish regression: bash is unavailable on this host.'
    }

    Push-Location "$root/backend"
    try {
        dotnet restore VisionStudio.slnx
        Assert-LastExitCode 'dotnet restore'

        dotnet run --project src/VisionStudio.CatalogExporter/VisionStudio.CatalogExporter.csproj --no-restore -- --check (Join-Path $root 'frontend/src/generated/catalog.generated.json')
        Assert-LastExitCode 'generated frontend catalog staleness check'

        dotnet test VisionStudio.slnx `
            --configuration Release `
            --no-restore `
            --filter "Category!=Soak" `
            --settings coverage.runsettings `
            --collect:"XPlat Code Coverage" `
            --results-directory $backendCoverage
        Assert-LastExitCode 'dotnet test'
    }
    finally { Pop-Location }

    & "$root/scripts/check-warning-baseline.ps1"
    Assert-LastExitCode 'warning baseline'

    & $python "$root/scripts/check-coverage.py" $backendCoverage --min-line $minCoverage --min-branch $minBranchCoverage
    Assert-LastExitCode 'coverage gate'

    Invoke-NuGetVulnerabilityGate "$root/backend/VisionStudio.slnx"
}

if ($runFrontend) {
    Push-Location "$root/frontend"
    try {
        if ($SkipInstall) {
            if (-not (Test-Path "$root/frontend/node_modules")) {
                Write-Host '-SkipInstall 需要现有 node_modules，但它不存在。去掉该参数重跑以安装依赖。' -ForegroundColor Red
                exit 1
            }
            Write-Host '[skip] npm ci（复用现有 node_modules；CI 不要加 -SkipInstall）' -ForegroundColor Yellow
        }
        else {
            npm ci --no-audit --no-fund
            Assert-LastExitCode 'npm ci'
        }

        npm audit --audit-level=high
        Assert-LastExitCode 'npm audit'

        npm run lint
        Assert-LastExitCode 'frontend lint'

        npm run check:theme
        Assert-LastExitCode 'frontend theme tokens'

        npm run test:coverage
        Assert-LastExitCode 'frontend tests + coverage'

        $coverageArchive = Join-Path $results 'frontend-coverage'
        New-Item $coverageArchive -ItemType Directory -Force | Out-Null
        $coverageTemp = Join-Path ([System.IO.Path]::GetTempPath()) 'visionstudio-frontend-coverage'
        foreach ($reportName in @('coverage-summary.json', 'lcov.info')) {
            $reportPath = Join-Path $coverageTemp $reportName
            if (Test-Path -LiteralPath $reportPath) {
                Copy-Item -LiteralPath $reportPath -Destination (Join-Path $coverageArchive $reportName) -Force
            }
            else {
                Write-Host "Coverage report missing: $reportPath" -ForegroundColor Yellow
            }
        }

        npm run build
        Assert-LastExitCode 'npm run build'

        & $python "$root/scripts/check-bundle-budget.py"
        Assert-LastExitCode 'bundle size budget'
    }
    finally { Pop-Location }
}

if ($runE2E) {
    Normalize-EnvironmentForStartProcess
    Invoke-E2ESmokeGate -Root $root -ResultsDir $results
}

Write-Host "Quality Gate PASSED (scope=$Scope)"
