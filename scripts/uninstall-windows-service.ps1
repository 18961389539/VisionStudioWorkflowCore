#Requires -RunAsAdministrator
<#
.SYNOPSIS
Uninstalls the VisionStudio.Api Windows service (data and packages are preserved).

.DESCRIPTION
Stops the service and waits for a graceful shutdown before deleting it. If the service does
not stop within the timeout, the script refuses to delete it: a forced kill can interrupt
database writes. The data root, publish directory and crash dumps are never deleted here.
#>
param(
    [string]$ServiceName = 'VisionStudioApi',
    [string]$EventSource = 'VisionStudio.Api',
    [switch]$RemoveEventSource
)

$ErrorActionPreference = 'Stop'

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Host "Service '$ServiceName' is not installed; nothing to do."
    exit 0
}

if ($service.Status -ne 'Stopped') {
    Write-Host "Stopping '$ServiceName' (graceful IHost shutdown, up to 120s)..."
    Stop-Service -Name $ServiceName
    try {
        (Get-Service -Name $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(120))
    }
    catch {
        Write-Host "Service '$ServiceName' did not stop within 120s. Refusing to delete a running service." -ForegroundColor Red
        Write-Host "Check '$ServiceName' logs and the Application event log, then retry. A forced kill can interrupt database writes."
        exit 1
    }
}

& sc.exe delete $ServiceName | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "sc.exe delete failed with exit code $LASTEXITCODE." -ForegroundColor Red
    exit 1
}

if ($RemoveEventSource) {
    try {
        Remove-EventLog -Source $EventSource -ErrorAction Stop
        Write-Host "Removed Application event-log source '$EventSource'."
    }
    catch {
        Write-Host "Event-log source '$EventSource' was not removed: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

Write-Host "Service '$ServiceName' deleted." -ForegroundColor Green
Write-Host "Data root, publish directory and crash dumps were NOT touched."