[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $CoverageRoot,

    [string] $AssemblyPattern,

    [double] $MinimumLineRate = 0,

    # Multi-target test runs emit one coverage file per target framework. Requiring a
    # minimum count surfaces a silently skipped target instead of merging the survivors.
    [int] $MinimumFiles = 0
)

$coverageFiles = Get-ChildItem -Path $CoverageRoot -Filter coverage.cobertura.xml -File -Recurse -ErrorAction SilentlyContinue
if ($coverageFiles.Count -eq 0) {
    throw "No Cobertura coverage file was found under '$CoverageRoot'."
}

Write-Output ("Coverage files: {0}" -f $coverageFiles.Count)
if ($MinimumFiles -gt 0 -and $coverageFiles.Count -lt $MinimumFiles) {
    throw ("Expected at least {0} coverage files under '{1}', found {2}; a target framework run may have been skipped." -f $MinimumFiles, $CoverageRoot, $coverageFiles.Count)
}

$selectedModules = @()
$coveredLines = 0
$totalLines = 0
foreach ($coverageFile in $coverageFiles) {
    [xml] $document = Get-Content -LiteralPath $coverageFile.FullName -Raw
    foreach ($package in @($document.coverage.packages.package)) {
        # Match the assembly package name rather than class file paths: coverlet changes
        # the reported source root (and therefore the filename prefix) depending on which
        # assemblies a test run covers, while the package name stays stable.
        if ($AssemblyPattern -and ([string] $package.name) -notmatch $AssemblyPattern) {
            continue
        }

        foreach ($class in @($package.classes.class)) {
            $moduleName = [string] $class.filename
            $selectedModules += $moduleName
            foreach ($line in @($class.lines.line)) {
                $totalLines++
                if ([int] $line.hits -gt 0) {
                    $coveredLines++
                }
            }
        }
    }
}

if ($totalLines -eq 0) {
    throw "Coverage files contained no lines after filtering."
}

$lineRate = $coveredLines / $totalLines
$percentage = $lineRate * 100
$uniqueModules = @($selectedModules | Sort-Object -Unique)
Write-Output ("Coverage: {0}/{1} lines ({2:N2}%), modules={3}" -f $coveredLines, $totalLines, $percentage, $uniqueModules.Count)

if ($MinimumLineRate -gt 0 -and $lineRate -lt $MinimumLineRate) {
    throw ("Coverage {0:P2} is below the required minimum {1:P2}." -f $lineRate, $MinimumLineRate)
}
