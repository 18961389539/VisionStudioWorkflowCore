[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version,
    [string] $PackageRoot = "artifacts/packages",
    [string] $DependencySource = "https://api.nuget.org/v3/index.json"
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$packagePath = (Resolve-Path -LiteralPath ([IO.Path]::GetFullPath($PackageRoot, $repoRoot))).Path
$tempRoot = (Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).Path
$smokeRoot = Join-Path $tempRoot ("imageviewer-package-smoke-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $smokeRoot | Out-Null

try {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'package-smoke/PackageSmoke.csproj'), (Join-Path $PSScriptRoot 'package-smoke/Program.cs') -Destination $smokeRoot
    $project = Join-Path $smokeRoot 'PackageSmoke.csproj'
    $nugetConfig = Join-Path $smokeRoot 'NuGet.Config'
    $escapedPackagePath = [Security.SecurityElement]::Escape($packagePath)
    $escapedDependencySource = [Security.SecurityElement]::Escape($DependencySource)
    @"
<configuration>
  <packageSources>
    <clear />
    <add key="local-build" value="$escapedPackagePath" />
    <add key="dependencies" value="$escapedDependencySource" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local-build">
      <package pattern="ImageViewerControl" />
      <package pattern="ImageViewer.Core" />
    </packageSource>
    <packageSource key="dependencies">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath $nugetConfig -Encoding utf8

    $versionProperty = "-p:ImageViewerPackageVersion=$Version"
    dotnet restore $project --configfile $nugetConfig --packages (Join-Path $smokeRoot 'packages') $versionProperty
    if ($LASTEXITCODE -ne 0) { throw "Package smoke restore failed." }
    $assets = Get-Content -LiteralPath (Join-Path $smokeRoot 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
    foreach ($packageId in @('ImageViewer.Core', 'ImageViewerControl')) {
        if ($assets.libraries["$packageId/$Version"].type -ne 'package') {
            throw "Expected package $packageId/$Version was not restored."
        }
    }

    dotnet build $project --configuration Release --no-restore --nologo $versionProperty
    if ($LASTEXITCODE -ne 0) { throw "Package smoke build failed." }

    $sessionPath = Join-Path $repoRoot 'ImageViewerDemo/Resources/MeasurementSample.ivsession'
    foreach ($framework in @('net8.0-windows', 'net10.0-windows')) {
        $assembly = Join-Path $smokeRoot "bin/Release/$framework/PackageSmoke.dll"
        dotnet $assembly $sessionPath (Join-Path $smokeRoot "$framework.csv")
        if ($LASTEXITCODE -ne 0) { throw "Package smoke failed on $framework." }
    }
}
finally {
    $resolvedSmokeRoot = (Resolve-Path -LiteralPath $smokeRoot).Path
    if ([string]::Equals((Split-Path -Parent $resolvedSmokeRoot), $tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedSmokeRoot) -match '^imageviewer-package-smoke-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $resolvedSmokeRoot -Recurse -Force
    }
}
