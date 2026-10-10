$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$results = Join-Path $root 'artifacts/test-results'
$minCoverage = if ($env:MIN_LINE_COVERAGE) { $env:MIN_LINE_COVERAGE } else { '20' }

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

if (Test-Path $results) { Remove-Item $results -Recurse -Force }
New-Item $results -ItemType Directory -Force | Out-Null

Write-Host "== VisionStudio V0.63 Quality Gate =="
& $python "$root/scripts/static-audit.py"
Assert-LastExitCode 'static audit'

# R08: production data must stay separated from the version package across publishes.
& "$root/scripts/test-publish-data-separation.ps1"
Assert-LastExitCode 'publish data separation'

# F07: the shared migration logic (staging + verification + marker + atomic swap) must reject
# interrupted retries and never place half data at the final path.
& "$root/scripts/test-data-migration.ps1"
Assert-LastExitCode 'data migration staging'

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

& $python "$root/scripts/check-coverage.py" $results --min-line $minCoverage
Assert-LastExitCode 'coverage gate'

Push-Location "$root/frontend"
try {
    npm install --no-audit --no-fund
    Assert-LastExitCode 'npm install'

    npm test
    Assert-LastExitCode 'frontend tests'

    npm run build
    Assert-LastExitCode 'npm run build'
}
finally { Pop-Location }

Write-Host "Quality Gate PASSED"
