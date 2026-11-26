[CmdletBinding()]
param(
    [ValidateRange(1, 10)]
    [int] $Runs = 3,
    [string] $ReportPath = 'artifacts/performance/batch/report.json'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $PSScriptRoot 'batch-performance'
$project = Join-Path $projectDir 'BatchPerformance.csproj'
$baseline = Get-Content -LiteralPath (Join-Path $projectDir 'baseline.json') -Raw | ConvertFrom-Json
$report = [IO.Path]::GetFullPath($ReportPath, $repoRoot)

function Get-Median([double[]] $Values) {
    $sorted = @($Values | Sort-Object)
    $lo = [int][Math]::Floor(($sorted.Count - 1) / 2)
    $hi = [int][Math]::Floor($sorted.Count / 2)
    return ($sorted[$lo] + $sorted[$hi]) / 2
}

Push-Location $repoRoot
try {
    dotnet restore $project --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Performance harness restore failed.' }
    dotnet build $project -c Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Performance harness build failed.' }
}
finally {
    Pop-Location
}

$assembly = Join-Path $projectDir 'bin/Release/net10.0-windows/BatchPerformance.dll'
$tempRoot = (Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).Path
$fixture = Join-Path $tempRoot ('imageviewer-batch-perf-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    dotnet $assembly prepare $fixture
    if ($LASTEXITCODE -ne 0) { throw 'Fixture generation failed.' }
    $samples = @(for ($trial = 1; $trial -le $Runs; $trial++) {
        $sampleJson = dotnet $assembly measure $fixture
        if ($LASTEXITCODE -ne 0) { throw "Batch measurement failed on trial $trial." }
        $sampleJson | ConvertFrom-Json
    })
    $medianPeak = Get-Median $samples.peakGrowthBytes
    $medianTime = Get-Median $samples.elapsedMilliseconds
    $hashMatches = @($samples | Where-Object { $_.csvSha256 -ne $baseline.expectedCsvSha256 }).Count -eq 0
    $memoryPass = $medianPeak -le ($baseline.maximumPeakGrowthMiB * 1MB)
    $reportDirectory = Split-Path -Parent $report
    New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
    [ordered]@{
        baseline = $baseline
        medianPeakGrowthMiB = $medianPeak / 1MB
        medianElapsedMilliseconds = $medianTime
        memoryPass = $memoryPass
        csvMatches = $hashMatches
        trials = $samples
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $report -Encoding utf8

    Write-Host ('Batch performance: {0:N1} MiB median peak growth, {1:N0} ms median; report: {2}' -f ($medianPeak / 1MB), $medianTime, $report)
    if ($medianTime -gt $baseline.slowRunMilliseconds) {
        Write-Warning 'Batch duration exceeded the observation threshold; check runner load and the performance report.'
    }
    if (!$memoryPass -or !$hashMatches) {
        throw 'Batch memory budget or normalized CSV baseline did not match. See the performance report.'
    }
}
finally {
    $resolved = (Resolve-Path -LiteralPath $fixture).Path
    if ((Split-Path -Parent $resolved) -eq $tempRoot -and
        (Split-Path -Leaf $resolved) -match '^imageviewer-batch-perf-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
