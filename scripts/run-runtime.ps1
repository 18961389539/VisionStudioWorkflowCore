$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$env:ASPNETCORE_ENVIRONMENT = 'Production'
if (-not $env:ASPNETCORE_URLS) { $env:ASPNETCORE_URLS = 'http://0.0.0.0:5080' }
dotnet "$root/artifacts/runtime-publish/VisionStudio.Api.dll"
