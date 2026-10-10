$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "[1/3] Publishing sample plugin..." -ForegroundColor Cyan
$pluginOut = Join-Path $root "backend/src/VisionStudio.Api/plugins/VisionStudio.Plugin.Sample"
New-Item -ItemType Directory -Force -Path $pluginOut | Out-Null
dotnet publish (Join-Path $root "backend/src/VisionStudio.Plugin.Sample/VisionStudio.Plugin.Sample.csproj") -c Debug -o $pluginOut

Write-Host "[2/3] Starting ASP.NET Core API..." -ForegroundColor Cyan
Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$root/backend/src/VisionStudio.Api'; `$env:ASPNETCORE_ENVIRONMENT='Development'; dotnet restore; dotnet run"

Write-Host "[3/3] Starting React/Vite frontend..." -ForegroundColor Cyan
Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$root/frontend'; npm install; npm run dev"

Write-Host "Open http://localhost:5173" -ForegroundColor Green
