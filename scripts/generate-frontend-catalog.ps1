$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'backend/src/VisionStudio.CatalogExporter/VisionStudio.CatalogExporter.csproj'
$output = Join-Path $root 'frontend/src/generated/catalog.generated.json'
dotnet run --project $project -- $output
if ($LASTEXITCODE -ne 0) { throw 'Catalog generation failed.' }
