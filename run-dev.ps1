param(
    [int]$Port = 5280,
    [switch]$SkipPlugin,
    [switch]$BackendOnly
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $SkipPlugin) {
    Write-Host "[1/3] Publishing sample plugin..." -ForegroundColor Cyan
    $pluginOut = Join-Path $root "backend/src/VisionStudio.Api/plugins/VisionStudio.Plugin.Sample"
    New-Item -ItemType Directory -Force -Path $pluginOut | Out-Null
    dotnet publish (Join-Path $root "backend/src/VisionStudio.Plugin.Sample/VisionStudio.Plugin.Sample.csproj") -c Debug -o $pluginOut
} else {
    Write-Host "[1/3] Skipping sample plugin publish (-SkipPlugin)." -ForegroundColor DarkGray
}

Write-Host "[2/3] Starting ASP.NET Core API via scripts/run-dev.ps1 (port $Port)..." -ForegroundColor Cyan
$backendScript = Join-Path $root "scripts/run-dev.ps1"
Start-Process powershell -ArgumentList "-NoExit", "-ExecutionPolicy", "Bypass", "-File", $backendScript, "-Port", "$Port"

if (-not $BackendOnly) {
    Write-Host "[3/3] Starting React/Vite frontend..." -ForegroundColor Cyan
    Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$root/frontend'; if (-not (Test-Path node_modules)) { npm install }; npm run dev"
    Write-Host "Open http://localhost:5273 (API: http://127.0.0.1:$Port)" -ForegroundColor Green
} else {
    Write-Host "API starting on http://127.0.0.1:$Port (-BackendOnly)." -ForegroundColor Green
}
