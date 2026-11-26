<#
.SYNOPSIS
    启动 VisionStudio WPF 桌面客户端，进程内直连引擎与图像内存。

.DESCRIPTION
    默认使用仓库内 third_party/ImageViewer 源码，不改变 Web 前后端。
    数据根默认为 artifacts/wpf-data，可通过 -DataRoot 或 VISIONSTUDIO_DATA_ROOT 覆盖。
    -ExternalImageViewerProject 可指定其他源码位置；
    -UseExternalImageViewer:$false 显式使用内建显示。
#>
param(
    [string]$DataRoot,
    [switch]$UseExternalImageViewer = $true,
    [string]$ExternalImageViewerProject
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not [string]::IsNullOrWhiteSpace($DataRoot)) {
    $env:VISIONSTUDIO_DATA_ROOT = $DataRoot
} elseif ([string]::IsNullOrWhiteSpace($env:VISIONSTUDIO_DATA_ROOT)) {
    $env:VISIONSTUDIO_DATA_ROOT = Join-Path $root 'artifacts\wpf-data'
}
New-Item $env:VISIONSTUDIO_DATA_ROOT -ItemType Directory -Force | Out-Null

$msbuildProps = @("-p:UseExternalImageViewer=$($UseExternalImageViewer.IsPresent.ToString().ToLowerInvariant())")
if (-not [string]::IsNullOrWhiteSpace($ExternalImageViewerProject)) {
    $msbuildProps += "-p:ExternalImageViewerProject=$ExternalImageViewerProject"
}

Write-Host "Starting VisionStudio.Wpf (In-Process Scheme C, DataRoot: $env:VISIONSTUDIO_DATA_ROOT)" -ForegroundColor Cyan
dotnet run --project (Join-Path $root 'wpf\src\VisionStudio.Wpf\VisionStudio.Wpf.csproj') @msbuildProps
exit $LASTEXITCODE
