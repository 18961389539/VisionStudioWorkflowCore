$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$id = [Guid]::NewGuid().ToString('N')
$version = Join-Path $root "artifacts\wpf-path-check-$id"
$data = Join-Path $root "artifacts\wpf-path-data-$id"
$outside = Join-Path ([System.IO.Path]::GetTempPath()) "wpf-path-check-$id"

function Assert-Rejected([scriptblock]$Action, [string]$Expected, [string]$Label) {
    try { & $Action }
    catch {
        if ($_.Exception.Message -notlike "*$Expected*") { throw }
        Write-Host "ok: $Label"
        return
    }
    throw "FAIL: $Label did not reject unsafe input."
}

Assert-Rejected { & "$PSScriptRoot\publish-wpf.ps1" -Output $outside -DataRoot $data } `
    'Output must be a version directory' 'publish outside artifacts'
Assert-Rejected { & "$PSScriptRoot\publish-wpf.ps1" -Output $version -DataRoot $version } `
    'Unsafe publish paths' 'publish data equals package'
Assert-Rejected { & "$PSScriptRoot\publish-wpf.ps1" -Output $version -DataRoot (Join-Path $version 'data') } `
    'Unsafe publish paths' 'publish data inside package'
Assert-Rejected { & "$PSScriptRoot\publish-wpf.ps1" -Output $version -DataRoot (Split-Path -Parent $version) } `
    'Unsafe publish paths' 'publish data contains package'
Assert-Rejected { & "$PSScriptRoot\run-wpf-runtime.ps1" -PublishDir $version -DataRoot $version } `
    'DataRoot must not overlap' 'runtime data equals package'
Assert-Rejected { & "$PSScriptRoot\run-wpf-runtime.ps1" -PublishDir $version -DataRoot (Join-Path $version 'data') } `
    'DataRoot must not overlap' 'runtime data inside package'
Assert-Rejected { & "$PSScriptRoot\test-wpf-publish.ps1" -PublishDir $version -DataRoot $version -Report (Join-Path $data 'report.json') } `
    'Smoke DataRoot must not overlap' 'smoke data equals package'
Assert-Rejected { & "$PSScriptRoot\test-wpf-publish.ps1" -PublishDir $version -DataRoot $data -Report (Join-Path $root 'LICENSE') } `
    'Use a new report path' 'smoke refuses stale evidence'

if ((Test-Path -LiteralPath $version) -or (Test-Path -LiteralPath $data) -or (Test-Path -LiteralPath $outside)) {
    throw 'FAIL: path rejection created output or data.'
}
Write-Host 'WPF PUBLISH PATHS PASSED'
exit 0
