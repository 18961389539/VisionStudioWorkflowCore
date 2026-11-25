function Resolve-PythonCommand {
    foreach ($candidate in @('py', 'python', 'python3')) {
        if (-not (Get-Command $candidate -ErrorAction SilentlyContinue)) { continue }
        $null = & $candidate --version 2>$null
        if ($LASTEXITCODE -eq 0) { return $candidate }
    }
    return $null
}

function Assert-StepExitCode([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Quality gate step failed: $step (exit code $LASTEXITCODE)." -ForegroundColor Red
        exit 1
    }
}

function Invoke-NuGetVulnerabilityGate([string]$SolutionPath) {
    # 已知漏洞扫描（2026-11-12 接入）。CPM 把"用哪个版本"收敛到一处，但"这个版本有没有已知漏洞"
    # 只能靠通告库回答；只拦 high/critical，避免内网工业环境 moderate/low 噪音。
    $vulnerabilityJson = (& dotnet list $SolutionPath package --vulnerable --include-transitive --format json 2>$null | Out-String)
    if ($LASTEXITCODE -ne 0) {
        # 离线机器查不了通告库：明确说"跳过"，不能把"查不了"静默当成"没问题"。
        Write-Host 'NuGet 漏洞扫描不可用（离线或源不可达），本次跳过。' -ForegroundColor Yellow
        return
    }
    $jsonStart = $vulnerabilityJson.IndexOf('{')
    if ($jsonStart -lt 0) {
        Write-Host 'NuGet 漏洞扫描未返回 JSON，本次跳过。' -ForegroundColor Yellow
        return
    }

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

function Invoke-E2ESmokeGate([string]$Root, [string]$ResultsDir) {
    $e2ePlaywrightPackage = Join-Path $Root 'frontend/node_modules/@playwright/test/package.json'
    if (-not (Test-Path -LiteralPath $e2ePlaywrightPackage)) {
        Write-Host 'E2E: frontend dependencies missing; running npm ci from the lockfile.'
        Push-Location "$Root/frontend"
        try {
            npm ci --no-audit --no-fund
            Assert-StepExitCode 'npm ci for e2e'
        }
        finally { Pop-Location }
    }

    $e2eApi = 'http://127.0.0.1:5199'
    $e2eData = Join-Path $Root ("artifacts/e2e-data/" + [guid]::NewGuid().ToString('N'))
    $e2eLogDir = Join-Path $ResultsDir 'e2e-api'
    New-Item $e2eData -ItemType Directory -Force | Out-Null
    New-Item $e2eLogDir -ItemType Directory -Force | Out-Null

    Push-Location "$Root/backend"
    try {
        dotnet build src/VisionStudio.Api/VisionStudio.Api.csproj -c Debug --nologo -v q
        Assert-StepExitCode 'backend build for e2e smoke'
    }
    finally { Pop-Location }

    $apiDll = Get-ChildItem (Join-Path $Root 'backend/src/VisionStudio.Api/bin/Debug') -Recurse -Filter 'VisionStudio.Api.dll' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $apiDll) {
        Write-Host 'E2E smoke: cannot locate VisionStudio.Api.dll after build.' -ForegroundColor Red
        exit 1
    }

    $previousEnvironment = @{
        ASPNETCORE_ENVIRONMENT = $env:ASPNETCORE_ENVIRONMENT
        ASPNETCORE_URLS       = $env:ASPNETCORE_URLS
        VISIONSTUDIO_DATA_ROOT = $env:VISIONSTUDIO_DATA_ROOT
        Security__AutoLoginAdmin = $env:Security__AutoLoginAdmin
    }
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS = $e2eApi
    $env:VISIONSTUDIO_DATA_ROOT = $e2eData
    $env:Security__AutoLoginAdmin = 'true'

    $apiProcess = Start-Process -FilePath 'dotnet' `
        -ArgumentList @("`"$($apiDll.FullName)`"", '--urls', $e2eApi) `
        -WorkingDirectory $apiDll.DirectoryName `
        -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $e2eLogDir 'host.out.log') `
        -RedirectStandardError (Join-Path $e2eLogDir 'host.err.log')
    try {
        $healthy = $false
        $deadline = (Get-Date).AddSeconds(120)
        while ((Get-Date) -lt $deadline) {
            if ($apiProcess.HasExited) { break }
            try {
                $probe = Invoke-WebRequest -Uri "$e2eApi/healthz" -UseBasicParsing -TimeoutSec 3
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

        $authStatus = Invoke-WebRequest -Uri "$e2eApi/api/auth/status" -UseBasicParsing -TimeoutSec 3
        Write-Host "E2E smoke: auth/status = $($authStatus.Content)"

        Push-Location "$Root/frontend"
        try {
            npx playwright install chromium --no-shell
            Assert-StepExitCode 'playwright browser install'

            $env:VISIONSTUDIO_E2E_API = $e2eApi
            $env:VISIONSTUDIO_E2E_OUTPUT_DIR = Join-Path ([System.IO.Path]::GetTempPath()) `
                ("visionstudio-e2e-output-" + [guid]::NewGuid().ToString('N'))
            npm run test:e2e
            Assert-StepExitCode 'frontend e2e'
        }
        finally {
            Remove-Item Env:\VISIONSTUDIO_E2E_API -ErrorAction SilentlyContinue
            Remove-Item Env:\VISIONSTUDIO_E2E_OUTPUT_DIR -ErrorAction SilentlyContinue
            Pop-Location
        }
    }
    finally {
        if ($apiProcess -and -not $apiProcess.HasExited) {
            Stop-Process -Id $apiProcess.Id -Force -ErrorAction SilentlyContinue
            $apiProcess.WaitForExit(5000) | Out-Null
        }
        foreach ($name in $previousEnvironment.Keys) {
            if ($null -eq $previousEnvironment[$name]) { Remove-Item "Env:\$name" -ErrorAction SilentlyContinue }
            else { Set-Item "Env:\$name" $previousEnvironment[$name] }
        }
    }
}
