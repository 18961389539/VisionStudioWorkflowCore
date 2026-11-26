param(
    [string]$PublishDir = $PSScriptRoot,
    [string]$DataRoot
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\PublishPaths.ps1')
$publish = [System.IO.Path]::GetFullPath($PublishDir)
if ([string]::IsNullOrWhiteSpace($DataRoot)) { $DataRoot = $env:VISIONSTUDIO_DATA_ROOT }
if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'VisionStudio\Trial\data'
}
$data = [System.IO.Path]::GetFullPath($DataRoot)
if (Test-PathOverlaps $publish $data) { throw 'DataRoot must not overlap the version package.' }
$exe = Join-Path $publish 'VisionStudio.Wpf.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Desktop executable not found: $exe" }
New-Item -ItemType Directory -Path $data -Force | Out-Null
$env:VISIONSTUDIO_DATA_ROOT = $data
$process = Start-Process -FilePath $exe -WorkingDirectory $publish -PassThru
$process.WaitForExit()
if ($process.ExitCode -ne 0) {
    throw "Desktop exited with code $($process.ExitCode). See '$data\logs'; check configuration and native prerequisites."
}
