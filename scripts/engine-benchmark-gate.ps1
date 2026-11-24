param(
  [string]$Fixtures = "",
  [string]$Baseline = "ci/engine-benchmark-baseline.json",
  [string]$Output = "artifacts/engine-benchmark-result.json",
  [int]$Samples = 9,
  [int]$Warmup = 2,
  [int]$TolerancePercent = 25,
  [string]$MachineLabel = "",
  [switch]$UpdateBaseline
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

function Resolve-RepoPath([string]$Path) {
  if ([System.IO.Path]::IsPathRooted($Path)) { return $Path }
  return Join-Path $root $Path
}

$project = Join-Path $root "backend/src/VisionStudio.Benchmark.Cli/VisionStudio.Benchmark.Cli.csproj"
$resolvedBaseline = Resolve-RepoPath $Baseline
$resolvedOutput = Resolve-RepoPath $Output

$benchArgs = @(
  "run", "--project", $project, "--configuration", "Release", "--",
  "--engine-bench",
  "--baseline", $resolvedBaseline,
  "--output", $resolvedOutput,
  "--samples", $Samples,
  "--warmup", $Warmup,
  "--tolerance-percent", $TolerancePercent
)
if ($Fixtures) { $benchArgs += @("--fixtures", (Resolve-RepoPath $Fixtures)) }
if ($MachineLabel) { $benchArgs += @("--machine-label", $MachineLabel) }
if ($UpdateBaseline) { $benchArgs += "--update-baseline" }

dotnet @benchArgs
$exitCode = $LASTEXITCODE
if ($exitCode -ne 0) {
  [Console]::Error.WriteLine("Engine benchmark exited with code $exitCode (10=regression, 11=benchmark failure, 12=config/infrastructure).")
}
exit $exitCode