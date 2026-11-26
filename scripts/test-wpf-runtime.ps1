param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [Parameter(Mandatory = $true)][string]$EvidenceDir
)
$ErrorActionPreference = 'Stop'
$publish = [System.IO.Path]::GetFullPath($PublishDir)
$evidence = [System.IO.Path]::GetFullPath($EvidenceDir)
. (Join-Path $PSScriptRoot 'lib\PublishPaths.ps1')
if (Test-PathOverlaps $publish $evidence) { throw 'Runtime evidence must not overlap the package.' }
if (Test-Path -LiteralPath $evidence) { throw 'Runtime checks require a new evidence directory.' }
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$exe = Join-Path $publish 'VisionStudio.Wpf.exe'
$previousDataRoot = $env:VISIONSTUDIO_DATA_ROOT
$launcher = $null
$application = $null

function Assert-StartupFailure([string]$Name, [string]$DataRoot, [string]$ExceptionType) {
    $report = Join-Path $evidence "$Name.json"
    $stderr = Join-Path $evidence "$Name.stderr.log"
    $env:VISIONSTUDIO_DATA_ROOT = $DataRoot
    $process = Start-Process -FilePath $exe -WorkingDirectory $publish `
        -ArgumentList @('--smoke-test', '--report', "`"$report`"") -PassThru `
        -RedirectStandardError $stderr
    try {
        if (-not $process.WaitForExit(30000)) { throw "Startup failure check timed out: $Name" }
        if ($process.ExitCode -eq 0 -or (Test-Path -LiteralPath $report)) {
            throw "Startup failure produced success-shaped output: $Name"
        }
        $errorText = Get-Content -LiteralPath $stderr -Raw -Encoding UTF8
        if ($errorText -notlike "*$ExceptionType*") { throw "Startup did not report the expected error: $Name" }
        Write-Host "ok: $Name rejected explicitly (exit $($process.ExitCode))"
    }
    finally {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    }
}

try {
    $occupied = Join-Path $evidence 'occupied-data'
    New-Item -ItemType Directory -Path $occupied -Force | Out-Null
    $lock = [System.IO.File]::Open((Join-Path $occupied '.wpf.lock'),
        [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    try { Assert-StartupFailure 'occupied-root' $occupied 'IOException' }
    finally { $lock.Dispose() }
    Assert-StartupFailure 'in-package-root' $publish 'InvalidOperationException'

    if (-not ('VisionStudioTrialWindowControl' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class VisionStudioTrialWindowControl {
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
'@
    }
    $launchData = Join-Path $evidence 'launcher-data'
    $ignoredData = Join-Path $evidence 'environment-data'
    $env:VISIONSTUDIO_DATA_ROOT = $ignoredData
    $launcher = Start-Process -FilePath 'powershell.exe' -WorkingDirectory $publish -PassThru `
        -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
            "`"$(Join-Path $publish 'Start-VisionStudio.ps1')`"", '-DataRoot', "`"$launchData`"") `
        -RedirectStandardOutput (Join-Path $evidence 'launcher.stdout.log') `
        -RedirectStandardError (Join-Path $evidence 'launcher.stderr.log')
    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $deadline) {
        if ($launcher.HasExited) { throw "Launcher exited before showing a window ($($launcher.ExitCode))." }
        if ($null -eq $application) {
            $child = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($launcher.Id) AND Name='VisionStudio.Wpf.exe'") |
                Select-Object -First 1
            if ($null -ne $child) { $application = Get-Process -Id $child.ProcessId }
        }
        if ($null -ne $application) {
            $application.Refresh()
            if ($application.HasExited) { throw 'Desktop exited before showing its main window.' }
            if ($application.MainWindowHandle -ne [IntPtr]::Zero) { break }
        }
        Start-Sleep -Milliseconds 200
    }
    if ($null -eq $application -or $application.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'Launcher did not create a responsive desktop window.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $launchData 'visionstudio.db')) -or (Test-Path -LiteralPath $ignoredData)) {
        throw 'Explicit launcher DataRoot did not take precedence over the environment.'
    }
    if (-not [VisionStudioTrialWindowControl]::PostMessage($application.MainWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)) {
        throw 'Could not request a normal close of the launched desktop.'
    }
    if (-not $launcher.WaitForExit(60000) -or $launcher.ExitCode -ne 0) {
        throw 'Launcher did not finish successfully after the normal window close.'
    }
    $released = [System.IO.File]::Open((Join-Path $launchData '.wpf.lock'),
        [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    $released.Dispose()
    @{ success = $true; normalLauncherClose = $true; explicitDataRootOverride = $true
       occupiedRootRejected = $true; inPackageRootRejected = $true; dataRoot = $launchData } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'runtime-checks.json') -Encoding UTF8
    Write-Host 'WPF RUNTIME CHECKS PASSED'
}
finally {
    if ($null -ne $application -and -not $application.HasExited) { Stop-Process -Id $application.Id -Force }
    if ($null -ne $launcher -and -not $launcher.HasExited) { Stop-Process -Id $launcher.Id -Force }
    $env:VISIONSTUDIO_DATA_ROOT = $previousDataRoot
}
exit 0
