param([string]$Output = "")
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $root 'artifacts/runtime-publish' }
$wwwroot = Join-Path $root 'backend/src/VisionStudio.Api/wwwroot'
if (Test-Path $wwwroot) { Remove-Item $wwwroot -Recurse -Force }
if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
Push-Location "$root/backend"
try {
    dotnet restore VisionStudio.slnx
    dotnet run --project src/VisionStudio.CatalogExporter/VisionStudio.CatalogExporter.csproj --no-restore -- --check (Join-Path $root 'frontend/src/generated/catalog.generated.json')
    if ($LASTEXITCODE -ne 0) { throw "Generated frontend catalog is stale." }
}
finally { Pop-Location }
Push-Location "$root/frontend"
try { npm install --no-audit --no-fund; npm run build } finally { Pop-Location }
New-Item $wwwroot -ItemType Directory -Force | Out-Null
Copy-Item "$root/frontend/dist/*" $wwwroot -Recurse -Force
Push-Location "$root/backend"
try { dotnet publish src/VisionStudio.Api/VisionStudio.Api.csproj -c Release --no-restore -o $Output } finally { Pop-Location }
Write-Host "Runtime published to: $Output"
