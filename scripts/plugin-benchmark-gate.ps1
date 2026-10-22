param(
  [Parameter(Mandatory=$true)][string]$Spec,
  [string]$Server = "http://127.0.0.1:5080",
  [string]$Output = "artifacts/plugin-benchmark-result.json",
  [string]$Junit = "artifacts/plugin-benchmark-junit.xml",
  [int]$TimeoutSeconds = 3600
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "backend/src/VisionStudio.Benchmark.Cli/VisionStudio.Benchmark.Cli.csproj"

dotnet run --project $project --configuration Release -- `
  --server $Server `
  --spec $Spec `
  --output $Output `
  --junit $Junit `
  --timeout-seconds $TimeoutSeconds
exit $LASTEXITCODE
