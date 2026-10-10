$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root 'artifacts/runtime-publish'
$env:ASPNETCORE_ENVIRONMENT = 'Production'
if (-not $env:ASPNETCORE_URLS) { $env:ASPNETCORE_URLS = 'http://0.0.0.0:5080' }
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
