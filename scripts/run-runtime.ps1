$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root 'artifacts/runtime-publish'
$env:ASPNETCORE_ENVIRONMENT = 'Production'
# R08: default to loopback only. Exposing the control port to the network requires TLS
# (HTTPS endpoint or a trusted reverse proxy) - plaintext HTTP leaks passwords and session
# cookies. The host refuses to start on a non-loopback http:// binding unless
# Security:AllowInsecureRemoteTransport=true (isolated test networks only).
if (-not $env:ASPNETCORE_URLS) { $env:ASPNETCORE_URLS = 'http://127.0.0.1:5080' }
# Production data must live outside the versioned publish directory, so upgrading to a new publish
# folder never migrates or recreates the database (publish-runtime.ps1 creates and migrates it):
#   $env:VISIONSTUDIO_DATA_ROOT = 'D:\VisionStudioData'
# Default to the same external data root used by publish-runtime.ps1 unless the operator overrides it.
if (-not $env:VISIONSTUDIO_DATA_ROOT) {
    $env:VISIONSTUDIO_DATA_ROOT = Join-Path $root 'artifacts/runtime-data'
}
New-Item $env:VISIONSTUDIO_DATA_ROOT -ItemType Directory -Force | Out-Null
# Pin the content root: config, static files and the data directory must not drift with the
# caller's working directory (the publish directory is used from any cwd).
dotnet "$publishDir/VisionStudio.Api.dll" --contentRoot "$publishDir"
