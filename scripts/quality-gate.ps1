$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$results = Join-Path $root 'artifacts/test-results'
$minCoverage = if ($env:MIN_LINE_COVERAGE) { $env:MIN_LINE_COVERAGE } else { '20' }
if (Test-Path $results) { Remove-Item $results -Recurse -Force }
New-Item $results -ItemType Directory -Force | Out-Null

Write-Host "== VisionStudio V0.63 Quality Gate =="
python "$root/scripts/static-audit.py"
if ($LASTEXITCODE -ne 0) { throw "Static audit failed." }
Push-Location "$root/backend"
try {
    dotnet restore VisionStudio.slnx
    dotnet run --project src/VisionStudio.CatalogExporter/VisionStudio.CatalogExporter.csproj --no-restore -- --check (Join-Path $root 'frontend/src/generated/catalog.generated.json')
    if ($LASTEXITCODE -ne 0) { throw "Generated frontend catalog is stale." }
    dotnet test VisionStudio.slnx `
        --configuration Release `
        --no-restore `
        --filter "Category!=Soak" `
        --settings coverage.runsettings `
        --collect:"XPlat Code Coverage" `
        --results-directory $results
}
finally { Pop-Location }

python "$root/scripts/check-coverage.py" $results --min-line $minCoverage
if ($LASTEXITCODE -ne 0) { throw "Coverage gate failed." }

Push-Location "$root/frontend"
try {
    npm install --no-audit --no-fund
    npm run build
}
finally { Pop-Location }

Write-Host "Quality Gate PASSED"
