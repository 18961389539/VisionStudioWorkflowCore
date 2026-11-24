#Requires -RunAsAdministrator
<#
.SYNOPSIS
Configures Windows Error Reporting (WER) LocalDumps for the VisionStudio host process.

.DESCRIPTION
Native crashes (access violation, stack overflow, fail-fast) terminate the process without a
managed exception. This script registers the per-process WER LocalDumps key so Windows writes
a dump file before the process dies. The key name is the process image name without ".exe":
"VisionStudio.Api" when the service runs the packaged apphost, "dotnet" if it runs the DLL.

DumpType: 1 = mini (small, default), 2 = full (large; only enable while actively diagnosing).
DumpCount bounds disk usage by keeping only the newest N dumps.

Managed unhandled exceptions can additionally be captured through the .NET runtime crash
dump environment variables (DOTNET_DbgEnableMiniDump / DOTNET_DbgMiniDumpType /
DOTNET_DbgMiniDumpName). They are intentionally not set machine-wide here: they affect every
.NET process on the machine. See docs/windows-service.md.
#>
param(
    [string]$ProcessName = 'VisionStudio.Api',
    [string]$DumpFolder = 'C:\ProgramData\VisionStudio\crash-dumps',
    [ValidateSet(1, 2)][int]$DumpType = 1,
    [ValidateRange(1, 100)][int]$DumpCount = 5,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$key = "HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\$ProcessName"

if ($Remove) {
    if (Test-Path $key) {
        Remove-Item $key -Recurse -Force
        Write-Host "Removed WER LocalDumps configuration for '$ProcessName'."
    }
    else {
        Write-Host "No WER LocalDumps configuration for '$ProcessName'; nothing to remove."
    }
    exit 0
}

$dumpFull = [System.IO.Path]::GetFullPath($DumpFolder).TrimEnd('\')
New-Item -ItemType Directory -Force -Path $dumpFull | Out-Null
New-Item -Path $key -Force | Out-Null
New-ItemProperty -Path $key -Name DumpFolder -PropertyType ExpandString -Value $dumpFull -Force | Out-Null
New-ItemProperty -Path $key -Name DumpType -PropertyType DWord -Value $DumpType -Force | Out-Null
New-ItemProperty -Path $key -Name DumpCount -PropertyType DWord -Value $DumpCount -Force | Out-Null

Write-Host "WER LocalDumps configured for '$ProcessName'." -ForegroundColor Green
Write-Host "  registry: $key"
Write-Host "  folder  : $dumpFull"
Write-Host "  type    : $DumpType (1=mini, 2=full)"
Write-Host "  keep    : $DumpCount newest dump(s)"
Write-Host ""
Write-Host "The service process must run the apphost EXE for the process name to be '$ProcessName'."
Write-Host "Reproduce the crash, then collect the newest .dmp file. Dumps may contain production"
Write-Host "images, credentials in memory, or customer data: handle them as sensitive artifacts."