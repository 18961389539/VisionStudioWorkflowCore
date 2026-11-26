param([string]$Output, [string]$DataRoot)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'lib\PublishPaths.ps1')
if ([string]::IsNullOrWhiteSpace($Output)) { $Output = Join-Path $root 'artifacts\wpf-publish' }
if ([string]::IsNullOrWhiteSpace($DataRoot)) { $DataRoot = Join-Path $root 'artifacts\wpf-data' }
$outputPath = [System.IO.Path]::GetFullPath($Output)
$allowed = [System.IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
if (-not $outputPath.StartsWith($allowed + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Output must be a version directory under '$allowed'."
}
$id = [Guid]::NewGuid().ToString('N')
$staging = "$outputPath.staging-$id"
$previous = "$outputPath.previous-$id"
$fullDataRoot = [System.IO.Path]::GetFullPath($DataRoot)
if (-not (Test-PublishPathsSeparated -Output $outputPath -Staging $staging -Previous $previous -DataRoot $fullDataRoot)) {
    throw 'Unsafe publish paths.'
}
$project = Join-Path $root 'wpf\src\VisionStudio.Wpf\VisionStudio.Wpf.csproj'
dotnet publish $project --configuration Release --runtime win-x64 --self-contained true `
    '-p:PublishSingleFile=false' '-p:PublishTrimmed=false' `
    '-p:ShouldUnsetParentConfigurationAndPlatform=false' --output $staging
if ($LASTEXITCODE -ne 0) { throw "WPF publish failed ($LASTEXITCODE); the previous package is unchanged." }
New-Item -ItemType Directory -Path (Join-Path $staging 'lib') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $staging 'licenses') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'run-wpf-runtime.ps1') -Destination (Join-Path $staging 'Start-VisionStudio.ps1')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'lib\PublishPaths.ps1') -Destination (Join-Path $staging 'lib\PublishPaths.ps1')
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $staging 'licenses\VisionStudio-LICENSE')
Copy-Item -LiteralPath (Join-Path $root 'third_party\ImageViewer\LICENSE') -Destination (Join-Path $staging 'licenses\ImageViewer-LICENSE')
Copy-Item -LiteralPath (Join-Path $root 'wpf\Delivery-README.txt') -Destination (Join-Path $staging 'README.txt')
$version = ([xml](Get-Content -LiteralPath $project -Raw -Encoding UTF8)).Project.PropertyGroup.Version
$commit = $null
$dirty = $null
if (Get-Command git -ErrorAction SilentlyContinue) {
    $commit = git -C $root rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { $commit = $null }
    $status = git -C $root status --porcelain
    if ($LASTEXITCODE -eq 0) { $dirty = -not [string]::IsNullOrWhiteSpace(($status -join "`n")) }
}
@{
    version = [string]$version; mode = 'InternalTrial'; runtimeIdentifier = 'win-x64'
    selfContained = $true; commit = $commit; dirty = $dirty
    sourceStatus = $(if ($null -eq $commit) { 'unknown' } else { 'git' })
    wpfSha256 = (Get-FileHash -LiteralPath (Join-Path $staging 'VisionStudio.Wpf.dll') -Algorithm SHA256).Hash
    apiSha256 = (Get-FileHash -LiteralPath (Join-Path $staging 'VisionStudio.Api.dll') -Algorithm SHA256).Hash
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $staging 'build-info.json') -Encoding UTF8
$evidence = Join-Path $root "artifacts\test-results\wpf-publish-$id"
$smokeData = Join-Path $evidence 'data'
& (Join-Path $PSScriptRoot 'test-wpf-publish.ps1') -PublishDir $staging -DataRoot $smokeData -Report (Join-Path $evidence 'first.json')
& (Join-Path $PSScriptRoot 'test-wpf-publish.ps1') -PublishDir $staging -DataRoot $smokeData -Report (Join-Path $evidence 'restart.json')
& (Join-Path $PSScriptRoot 'test-wpf-runtime.ps1') -PublishDir $staging -EvidenceDir (Join-Path $evidence 'runtime')
if (Test-Path -LiteralPath $outputPath) { Move-Item -LiteralPath $outputPath -Destination $previous }
try { Move-Item -LiteralPath $staging -Destination $outputPath }
catch {
    if (Test-Path -LiteralPath $previous) { Move-Item -LiteralPath $previous -Destination $outputPath }
    throw
}
Write-Host "Verified desktop package: $outputPath"
Write-Host "Evidence: $evidence"
Write-Host "Previous package retained (if any): $previous"
