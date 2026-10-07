$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$pluginOut = Join-Path $root "backend/src/VisionStudio.Api/plugins/sample.math"
if (Test-Path $pluginOut) { Remove-Item -Recurse -Force $pluginOut }
New-Item -ItemType Directory -Force -Path $pluginOut | Out-Null
dotnet publish (Join-Path $root "backend/src/VisionStudio.Plugin.Sample/VisionStudio.Plugin.Sample.csproj") -c Debug -o $pluginOut
Write-Host "SDK 2.0 sample plugin installed to $pluginOut. Use Plugins > Rescan while Production is stopped, or restart the API." -ForegroundColor Green
