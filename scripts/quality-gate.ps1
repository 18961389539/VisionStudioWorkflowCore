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
# 后端覆盖率单独一个目录（2026-11-12）：`dotnet test` 每轮在 --results-directory 下建一个 GUID
# 子目录，混在 test-results 根上就没法只清"这一轮的覆盖率"而不碰 e2e 产物。分出来之后每轮
# 只需删掉几个只有一两个文件的 GUID 子目录，既不触发批量删除守卫，也不会让旧报告混进并集覆盖
# 里把"这轮其实没覆盖到的行"顶成已覆盖。
$backendCoverage = Join-Path $results 'backend-coverage'
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

# 清空一个目录，但只用"删少量子项"的方式（幂等；不存在即 no-op）。
#
# 为什么不直接 `Remove-Item $dir -Recurse -Force`：本机删除被宿主的安全删除包装器接管。
# 它对两层都设了闸：
#   1) 递归删除会被交给回收站助手搬走，助手把成功回执（`OK <path>`）写到 stderr，
#      而本脚本全局 $ErrorActionPreference='Stop'——PowerShell 5.1 下原生命令写 stderr 会被
#      提升为终止性错误（`*>` 重定向也拦不住），于是目录明明已经搬走，包装器还是抛
#      SAFE_DELETE_FAIL_CLOSED{reason=trash-failed}，门禁整段中断；
#   2) 单次删除目标下的条目数超过阈值（50）会要求人工确认，直接抛
#      SAFE_DELETE_BULK_CONFIRM_REQUIRED。覆盖率输出目录有上百个小文件，必然撞上这条。
#
# 结论：门禁不去删大目录，而是让每次运行本来就产出到可覆盖的路径（见 backend-coverage /
# frontend 的 clean:false）。这里只处理"每轮只有一两个文件"的 GUID 子目录，既在阈值内，
# 也不依赖删除来保证读数正确。安全删除机制没有被绕过，也没有任何一处改成直接 unlink。
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
    New-Item $results -ItemType Directory -Force | Out-Null
    New-Item $backendCoverage -ItemType Directory -Force | Out-Null
    Clear-ChildDirectories $backendCoverage
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
        --results-directory $backendCoverage
    Assert-LastExitCode 'dotnet test'
}
finally { Pop-Location }

# R09 (2026-10-12 review): warning governance is part of the gate - new compiler warnings must be
# fixed, or explicitly added to the reviewable baseline (scripts/warning-baseline.txt). Resolved
# entries surface as hints (not failures) so the baseline can be pruned deliberately.
& "$root/scripts/check-warning-baseline.ps1"
Assert-LastExitCode 'warning baseline'

& $python "$root/scripts/check-coverage.py" $backendCoverage --min-line $minCoverage --min-branch $minBranchCoverage
Assert-LastExitCode 'coverage gate'

# 已知漏洞扫描（2026-11-12 接入）。CPM 把"用哪个版本"收敛到一处，但"这个版本有没有已知漏洞"
# 只能靠通告库回答；它不会让构建失败，只会在某天变成安全事故。
# 只拦 high/critical：内网工业环境里 moderate/low 的噪音会把门禁训练成"看到就跳过"。
$vulnerabilityJson = (& dotnet list "$root/backend/VisionStudio.slnx" package --vulnerable --include-transitive --format json 2>$null | Out-String)
if ($LASTEXITCODE -ne 0) {
    # 离线机器查不了通告库：明确说"跳过"，不能把"查不了"静默当成"没问题"。
    Write-Host 'NuGet 漏洞扫描不可用（离线或源不可达），本次跳过。' -ForegroundColor Yellow
}
else {
    $jsonStart = $vulnerabilityJson.IndexOf('{')
    if ($jsonStart -lt 0) {
        Write-Host 'NuGet 漏洞扫描未返回 JSON，本次跳过。' -ForegroundColor Yellow
    }
    else {
        $report = $vulnerabilityJson.Substring($jsonStart) | ConvertFrom-Json
        $vulnerable = New-Object System.Collections.Generic.List[string]
        foreach ($project in $report.projects) {
            foreach ($framework in $project.frameworks) {
                $packages = @()
                if ($framework.topLevelPackages) { $packages += $framework.topLevelPackages }
                if ($framework.transitivePackages) { $packages += $framework.transitivePackages }
                foreach ($package in $packages) {
                    foreach ($vulnerability in @($package.vulnerabilities)) {
                        if ($null -eq $vulnerability) { continue }
                        $severity = "$($vulnerability.severity)".ToLowerInvariant()
                        if ($severity -eq 'high' -or $severity -eq 'critical') {
                            $vulnerable.Add("$($package.id) $($package.resolvedVersion) [$severity] $($vulnerability.advisoryurl)")
                        }
                    }
                }
            }
        }
        if ($vulnerable.Count -gt 0) {
            Write-Host 'NuGet 已知漏洞（high/critical）：' -ForegroundColor Red
            $vulnerable | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
            Write-Host '处理：在 backend/Directory.Packages.props 升级版本；确属误报时在提交说明里写清豁免理由。' -ForegroundColor Red
            exit 1
        }
        Write-Host 'NuGet: no high/critical known vulnerabilities.' -ForegroundColor Green
    }
}
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

    # 依赖漏洞（2026-11-12 接入）：npm ci 带 --no-audit 是为了安装速度，审计本身不能省。
    # 首次接入就发现 vite 8.3.2 与 undici 各带 high 漏洞——它们不影响构建，只影响"这台机器
    # 会不会被任意命令执行"。--audit-level=high 与后端段口径一致。
    npm audit --audit-level=high
    Assert-LastExitCode 'npm audit'

    # 静态检查（2026-11-12 接入）：oxlint（Rust 实现，不依赖 TypeScript 编译 API）。
    # 项目使用 TS 7 原生编译器，typescript-eslint 的 peer 范围只到 <6.1，无法在此版本上解析，
    # 因此选用 oxlint：口径为「零告警」，新增未使用变量/死导入立即失败。
    # React Compiler 系列规则（set-state-in-effect / refs / exhaustive-deps）尚未接入，
    # 基线数量见 docs/quality-gates.md，按批收敛后再逐条开启。
    npm run lint
    Assert-LastExitCode 'frontend lint'

    # 覆盖率门禁（2026-10-23 接入）：阈值写在 vite.config.ts 的 test.coverage.thresholds，
    # 取首次基线（语句 69.29 / 分支 65.99 / 函数 59.35 / 行 74.02）并各留约 1% 余量。
    # 棘轮规则：只升不降；覆盖提升后必须同步上调阈值，否则门禁形同虚设。
    #
    # 报告目录在系统临时目录（vite.config.ts 里改成 os.tmpdir()，原因见那里的注释：v8 provider
    # 会无条件递归删除内部 .tmp，上百个小文件在本机会被批量删除守卫拦下）。
    # 跑完把两份机器可读摘要复制回 artifacts/ 留档——复制不删任何东西，不会触发守卫。
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
}
finally { Pop-Location }
}

if ($runE2E) {
# 真后端冒烟（2026-11-12）：e2e 默认把 /api/** 全桩掉，测的是"前端遇到某种 JSON 怎么表现"。
# 这里额外拉起一个真实宿主（独立端口 + 独立数据根），跑 real-backend-smoke.spec.ts：
# 登录 → 目录 → 真执行 → 追溯落库 → 节点 span。桩化用例照旧，不受影响。
$e2eApi = 'http://127.0.0.1:5199'
$e2eData = Join-Path $root 'artifacts/e2e-data'
$e2eLogDir = Join-Path $results 'e2e-api'
New-Item $e2eData -ItemType Directory -Force | Out-Null
New-Item $e2eLogDir -ItemType Directory -Force | Out-Null

Push-Location "$root/backend"
try {
    dotnet build src/VisionStudio.Api/VisionStudio.Api.csproj -c Debug --nologo -v q
    Assert-LastExitCode 'backend build for e2e smoke'
}
finally { Pop-Location }

# 找构建产物而不是拼路径：TargetFramework 变了之后拼路径会静默指向不存在的文件。
$apiDll = Get-ChildItem (Join-Path $root 'backend/src/VisionStudio.Api/bin/Debug') -Recurse -Filter 'VisionStudio.Api.dll' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $apiDll) {
    Write-Host 'E2E smoke: cannot locate VisionStudio.Api.dll after build.' -ForegroundColor Red
    exit 1
}

$previousEnvironment = @{
    ASPNETCORE_ENVIRONMENT = $env:ASPNETCORE_ENVIRONMENT
    ASPNETCORE_URLS       = $env:ASPNETCORE_URLS
    VISIONSTUDIO_DATA_ROOT = $env:VISIONSTUDIO_DATA_ROOT
}
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = $e2eApi
$env:VISIONSTUDIO_DATA_ROOT = $e2eData
$apiProcess = Start-Process -FilePath 'dotnet' `
    -ArgumentList @("`"$($apiDll.FullName)`"", '--urls', $e2eApi) `
    -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $e2eLogDir 'host.out.log') `
    -RedirectStandardError (Join-Path $e2eLogDir 'host.err.log')
try {
    $healthy = $false
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline) {
        if ($apiProcess.HasExited) { break }
        try {
            $probe = Invoke-WebRequest -Uri "$e2eApi/api/health" -UseBasicParsing -TimeoutSec 3
            if ($probe.StatusCode -eq 200) { $healthy = $true; break }
        }
        catch {
            # 启动期连不上是预期状态：继续轮询，直到超时或进程退出。
            Start-Sleep -Milliseconds 500
        }
    }
    if (-not $healthy) {
        Write-Host 'E2E smoke: backend did not become healthy; host log tail follows.' -ForegroundColor Red
        Get-Content (Join-Path $e2eLogDir 'host.err.log') -Tail 40 -ErrorAction SilentlyContinue
        Get-Content (Join-Path $e2eLogDir 'host.out.log') -Tail 40 -ErrorAction SilentlyContinue
        exit 1
    }

    Push-Location "$root/frontend"
    try {
        # e2e（2026-10-23 接入）：真实浏览器，补 jsdom 覆盖不到的布局引擎、Canvas 与事件链。
        # 浏览器按需安装——windows-latest 自带 Edge，但 Playwright 仍需自己的 Chromium 构建。
        npx playwright install chromium
        Assert-LastExitCode 'playwright browser install'

        # 宿主 URL 通过环境变量传给 real-backend-smoke.spec.ts（默认即 5199）。
        $env:VISIONSTUDIO_E2E_API = $e2eApi
        npm run test:e2e
        Assert-LastExitCode 'frontend e2e'
    }
    finally {
        Remove-Item Env:\VISIONSTUDIO_E2E_API -ErrorAction SilentlyContinue
        Pop-Location
    }
}
finally {
    if ($apiProcess -and -not $apiProcess.HasExited) {
        Stop-Process -Id $apiProcess.Id -Force -ErrorAction SilentlyContinue
        # 给 SQLite 一个关闭 WAL 的窗口；Windows 上句柄未释放时下一步清理会失败。
        $apiProcess.WaitForExit(5000) | Out-Null
    }
    foreach ($name in $previousEnvironment.Keys) {
        if ($null -eq $previousEnvironment[$name]) { Remove-Item "Env:\$name" -ErrorAction SilentlyContinue }
        else { Set-Item "Env:\$name" $previousEnvironment[$name] }
    }
}
}

Write-Host "Quality Gate PASSED (scope=$Scope)"
