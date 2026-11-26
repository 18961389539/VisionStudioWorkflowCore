param(
    [string]$DataRoot,
    [switch]$UseExternalImageViewer = $true,
    [string]$ExternalImageViewerProject
)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'scripts\run-wpf.ps1') @PSBoundParameters
exit $LASTEXITCODE
