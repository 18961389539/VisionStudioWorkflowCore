param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [Parameter(Mandatory = $true)][string]$DataRoot,
    [Parameter(Mandatory = $true)][string]$Report
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\PublishPaths.ps1')
$publish = [System.IO.Path]::GetFullPath($PublishDir)
$data = [System.IO.Path]::GetFullPath($DataRoot)
$reportPath = [System.IO.Path]::GetFullPath($Report)
if (Test-PathOverlaps $publish $data) { throw 'Smoke DataRoot must not overlap the package.' }
if (Test-Path -LiteralPath (Join-Path $publish 'data')) { throw 'Package contains runtime data copied from the source host.' }
if (Test-Path -LiteralPath $reportPath) { throw 'Use a new report path; stale smoke results are not accepted.' }
New-Item -ItemType Directory -Path $data -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $reportPath) -Force | Out-Null
$env:VISIONSTUDIO_DATA_ROOT = $data
$exe = Join-Path $publish 'VisionStudio.Wpf.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Missing executable: $exe" }
$process = Start-Process -FilePath $exe -WorkingDirectory $publish `
    -ArgumentList @('--smoke-test', '--report', "`"$reportPath`"") -PassThru
if (-not $process.WaitForExit(120000)) {
    Stop-Process -Id $process.Id -Force
    throw "Desktop smoke timed out. Inspect '$data\logs'."
}
if ($process.ExitCode -ne 0) { throw "Desktop smoke failed ($($process.ExitCode)). Inspect '$data\logs'." }
if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) { throw 'Desktop did not write its report.' }
$result = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not [string]::Equals($result.contentRoot.TrimEnd('\'), $publish.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) `
    -or -not [string]::Equals($result.runtimeDirectory.TrimEnd('\'), $publish.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) `
    -or -not [string]::Equals($result.dataRoot.TrimEnd('\'), $data.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Smoke used a source content root, external runtime, or unexpected data root.'
}
if ($result.success -ne $true -or $result.publishedVersion -ne 1 -or $result.previewAvailable -ne $true `
    -or [string]::IsNullOrWhiteSpace($result.runId) -or $result.cameraSequence -lt 1) {
    throw 'Desktop smoke output does not satisfy the acceptance contract.'
}
if (-not (Test-Path -LiteralPath $result.diagnosticPath -PathType Leaf)) { throw 'Diagnostic bundle missing.' }
if (-not (Test-Path -LiteralPath (Join-Path $data 'workflows\wpf-publish-smoke.json') -PathType Leaf)) {
    throw 'Saved workflow missing after process exit.'
}
if (Test-Path -LiteralPath (Join-Path $publish 'data')) { throw 'Desktop wrote runtime data into the package.' }
Write-Host "Desktop smoke passed: $reportPath"
