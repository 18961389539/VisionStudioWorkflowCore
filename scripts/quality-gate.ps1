<#
.SYNOPSIS
    VisionStudio 质量门禁。

.DESCRIPTION
    2026-11-12：增加 -Scope，让 CI 把一条 25 分钟的串行流水线拆成可并行的三段：

      Backend  静态审计 + 度量下限 + 发布/迁移回归 + dotnet test + 告警基线 + 覆盖率
      Frontend npm ci + lint + 单测与覆盖率 + 生产构建
      E2E      Playwright 真浏览器回归
      All（默认，本地用）以上全部，按原顺序执行

    拆分只是把同一套检查分到不同 runner 并行跑，不降低任何口径：CI 里三段全绿才算通过。
    直接跑 All 仍然是本地提交前的完整验证。

    -SkipInstall：仅用于本地反复跑门禁时复用已有 node_modules。npm ci 会先删空 node_modules
    再全量重装，一次几分钟；调一个断言就要重装一遍是纯浪费。CI 必须保持默认（不加此开关），
    否则本地残留依赖可能掩盖 package-lock.json 漂移——这是这个开关唯一但不小的代价。
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
$runBackend = $Scope -in @('All', 'Backend')
$runFrontend = $Scope -in @('All', 'Frontend')
$runE2E = $Scope -in @('All', 'E2E')

# 覆盖率棘轮（只升不降）。2026-10-29 实测 83.47% 合并行覆盖率，取 81 留约 2.5 点余量：
# 原默认 60 只能防"腰斩"，挡不住每次合并掉零点几个点的慢性退化，最终仍会滑到 60 附近。
# 提升覆盖后必须同步上调此值与 workflow 里的 MIN_LINE_COVERAGE。
$minCoverage = if ($env:MIN_LINE_COVERAGE) { $env:MIN_LINE_COVERAGE } else { '81' }
# 分支覆盖率棘轮（2026-10-29 接入）：实测 62.04%，取 60 留约 2 点余量。
# 行覆盖率挡不住"行走了、分支只走一边"——`if (x) return;` 走一次就算整行覆盖。
$minBranchCoverage = if ($env:MIN_BRANCH_COVERAGE) { $env:MIN_BRANCH_COVERAGE } else { '60' }

# Native commands (python / dotnet / npm) do not raise terminating errors through
# ErrorActionPreference: every native step must check its exit code and abort with a non-zero
# code immediately. Without the guard a failed step still reaches "Quality Gate PASSED"
# (a real Windows case: `python` missing -> exit code 9009 was silently ignored here).
function Assert-LastExitCode([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Quality gate step failed: $step (exit code $LASTEXITCODE)." -ForegroundColor Red
        exit 1
    }
}

# Resolve a Python interpreter: on Windows the command is usually py / python / python3.
# The gate requires it for the static audit and coverage checks - fail instead of skipping them.
$python = $null
foreach ($candidate in @('py', 'python', 'python3')) {
    if (Get-Command $candidate -ErrorAction SilentlyContinue) { $python = $candidate; break }
}
if ($null -eq $python) {
    Write-Host 'Python interpreter not found (py / python / python3). The quality gate requires it for static audit and coverage checks.' -ForegroundColor Red
    exit 1
}

if ($runBackend) {
    if (Test-Path $results) { Remove-Item $results -Recurse -Force }
    New-Item $results -ItemType Directory -Force | Out-Null
}

Write-Host "== VisionStudio V0.63 Quality Gate (scope=$Scope) =="
if (-not $runBackend) {
    # 静态审计 / 度量下限 / 发布回归是按仓库整体校验的跨切面检查，只在 Backend 段跑一次：
    # 并行时重复执行会同时读写 artifacts 目录，互相踩。
    Write-Host 'Skipping repository-wide checks (static audit / metric floor / publish regressions): scope is not Backend.'
}
if ($runBackend) {
& $python "$root/scripts/static-audit.py"
Assert-LastExitCode 'static audit'

# 度量下限棘轮（2026-10-29）：节点目录与测试数量只许升不许降。
# 覆盖率门槛挡不住"删测试"——被删的往往正是难覆盖的分支，删完覆盖率反而更高，门禁更绿。
# 数量下限是唯一能发现"测试悄悄消失"的信号，基线见 scripts/metric-floor.json。
& $python "$root/scripts/check-metric-floor.py"
Assert-LastExitCode 'metric floor'

# 文件规模预算（2026-11-12）：拆分是一次性动作，没有预算就会长回去。
# 只许降不许升；新增超 1200 行的文件直接失败（scripts/size-budget.json）。
& $python "$root/scripts/check-size-budget.py"
Assert-LastExitCode 'size budget'

# R08: production data must stay separated from the version package across publishes.
& "$root/scripts/test-publish-data-separation.ps1"
Assert-LastExitCode 'publish data separation'

# F07: the shared migration logic (staging + verification + marker + atomic swap) must reject
# interrupted retries and never place half data at the final path.
& "$root/scripts/test-data-migration.ps1"
Assert-LastExitCode 'data migration staging'

# Q09: the Bash publish path (staging + in-package data migration + atomic swap + rollback) carries
# the same safety contract as the Windows script and ships its own regression suite. Run it when a
# bash shell is available (Git Bash on windows-latest runners); skip with an explicit message otherwise.
$bash = Get-Command bash -ErrorAction SilentlyContinue
if ($bash) {
    & bash "$root/scripts/test-publish-runtime.sh"
    Assert-LastExitCode 'bash publish regression'
} else {
    Write-Host 'Skipping bash publish regression: bash is unavailable on this host.'
}
}

if ($runBackend) {
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
        --results-directory $results
    Assert-LastExitCode 'dotnet test'
}
finally { Pop-Location }

# R09 (2026-10-12 review): warning governance is part of the gate - new compiler warnings must be
# fixed, or explicitly added to the reviewable baseline (scripts/warning-baseline.txt). Resolved
# entries surface as hints (not failures) so the baseline can be pruned deliberately.
& "$root/scripts/check-warning-baseline.ps1"
Assert-LastExitCode 'warning baseline'

& $python "$root/scripts/check-coverage.py" $results --min-line $minCoverage --min-branch $minBranchCoverage
Assert-LastExitCode 'coverage gate'
}

if ($runFrontend) {
Push-Location "$root/frontend"
try {
    # 依赖安装：默认 npm ci（CI 口径，锁文件必须可信）。
    # -SkipInstall 时只做存在性检查——缺 node_modules 就明确报错，
    # 而不是让后面的 lint / vitest 抛一堆 "Cannot find module" 让人误判成代码坏了。
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

    # 静态检查（2026-11-12 接入）：oxlint（Rust 实现，不依赖 TypeScript 编译 API）。
    # 项目使用 TS 7 原生编译器，typescript-eslint 的 peer 范围只到 <6.1，无法在此版本上解析，
    # 因此选用 oxlint：口径为「零告警」，新增未使用变量/死导入立即失败。
    # React Compiler 系列规则（set-state-in-effect / refs / exhaustive-deps）尚未接入，
    # 基线数量见 docs/quality-gates.md，按批收敛后再逐条开启。
    npm run lint
    Assert-LastExitCode 'frontend lint'

    # 覆盖率输出目录先由 PowerShell 清掉，再交给 vitest。
    # vitest 的 v8 provider 默认在开跑前自己递归删除 reportsDirectory（clean: true）。
    # 那个目录里 lcov-report 是上百个小文件，放在 node 进程里删有两个现实问题：
    #   1) Windows + 杀软/EDR 实时扫描下很慢，且可能触发"批量删除"策略直接拦下整个步骤；
    #   2) 拦下时 vitest 抛的是 unhandled error，日志只剩一行 RUN，看起来像测试自己崩了。
    # 用 PowerShell 删（不经过 node）既快又稳定；目录不存在时 Test-Path 天然 no-op。
    # 注：-Scope All 时后端段已清空整个 test-results，这里再判一次只是幂等兜底。
    $coverageDir = "$root/artifacts/test-results/frontend-coverage"
    if (Test-Path $coverageDir) { Remove-Item $coverageDir -Recurse -Force }

    # 覆盖率门禁（2026-10-23 接入）：阈值写在 vite.config.ts 的 test.coverage.thresholds，
    # 取首次基线（语句 69.29 / 分支 65.99 / 函数 59.35 / 行 74.02）并各留约 1% 余量。
    # 棘轮规则：只升不降；覆盖提升后必须同步上调阈值，否则门禁形同虚设。
    npm run test:coverage
    Assert-LastExitCode 'frontend tests + coverage'

    npm run build
    Assert-LastExitCode 'npm run build'
}
finally { Pop-Location }
}

if ($runE2E) {
Push-Location "$root/frontend"
try {
    # e2e（2026-10-23 接入）：真实浏览器，补 jsdom 覆盖不到的布局引擎、Canvas 与事件链。
    # 浏览器按需安装——windows-latest 自带 Edge，但 Playwright 仍需自己的 Chromium 构建。
    npx playwright install chromium
    Assert-LastExitCode 'playwright browser install'

    npm run test:e2e
    Assert-LastExitCode 'frontend e2e'
}
finally { Pop-Location }
}

Write-Host "Quality Gate PASSED (scope=$Scope)"
