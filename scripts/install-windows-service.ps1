#Requires -RunAsAdministrator
<#
.SYNOPSIS
Installs VisionStudio.Api as a Windows service.

.DESCRIPTION
The service runs the apphost EXE from the publish directory, which keeps the process name
stable ("VisionStudio.Api") for WER crash-dump collection. Production data lives outside the
version package: replacing the publish directory on upgrade must never touch the database.

Stop/shutdown control flows through Microsoft.Extensions.Hosting.WindowsServices
(WindowsServiceLifetime), so SCM stop requests trigger IHost graceful shutdown (database
writes settle, rolling logs drain) instead of a hard process kill.

.PARAMETER AllowInsecureUrl
Opt-in for isolated test networks: permits a non-loopback plaintext URL and forwards
--Security:AllowInsecureRemoteTransport=true. Production deployments should bind loopback
and terminate TLS in front of the host (HTTPS endpoint or trusted reverse proxy).
#>
param(
    [string]$ServiceName = 'VisionStudioApi',
    [string]$DisplayName = 'VisionStudio API',
    [string]$PublishDir = '',
    [string]$DataRoot = '',
    [string]$Url = 'http://127.0.0.1:5080',
    [string]$EventSource = 'VisionStudio.Api',
    [switch]$AllowInsecureUrl,
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $PublishDir) { $PublishDir = Join-Path $root 'artifacts/runtime-publish' }
if (-not $DataRoot) { $DataRoot = Join-Path $root 'artifacts/runtime-data' }
$publishFull = [System.IO.Path]::GetFullPath($PublishDir).TrimEnd('\')
$dataFull = [System.IO.Path]::GetFullPath($DataRoot).TrimEnd('\')
$exePath = Join-Path $publishFull 'VisionStudio.Api.exe'

if (-not (Test-Path $exePath)) {
    Write-Host "VisionStudio.Api.exe not found in '$publishFull'. Run scripts/publish-runtime.ps1 first." -ForegroundColor Red
    exit 1
}
if (-not (Test-Path (Join-Path $publishFull 'appsettings.json'))) {
    Write-Host "appsettings.json not found in '$publishFull'. The package is incomplete." -ForegroundColor Red
    exit 1
}
if (-not (Test-Path (Join-Path $publishFull 'wwwroot/index.html'))) {
    Write-Host "Warning: wwwroot/index.html is missing; the UI will not be served by this package." -ForegroundColor Yellow
}

if (-not $AllowInsecureUrl -and $Url -notmatch '^https://' -and $Url -notmatch '^http://(127\.0\.0\.1|localhost)(:\d+)?/?$') {
    Write-Host "Refusing a non-loopback plaintext URL '$Url': passwords and session cookies would travel unprotected." -ForegroundColor Red
    Write-Host "Bind loopback (http://127.0.0.1:5080) and terminate TLS in front, or pass -AllowInsecureUrl for an isolated test network."
    exit 1
}

# The upgrade flow replaces the publish directory; a data root inside it would be deleted with it.
if ($dataFull -eq $publishFull -or $dataFull.StartsWith($publishFull + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "DataRoot must live outside the version package '$publishFull' (got '$dataFull')." -ForegroundColor Red
    exit 1
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Service '$ServiceName' already exists. Run scripts/uninstall-windows-service.ps1 first, or choose another -ServiceName." -ForegroundColor Red
    exit 1
}

New-Item -ItemType Directory -Force -Path $dataFull | Out-Null
try {
    New-EventLog -LogName Application -Source $EventSource -ErrorAction Stop
    Write-Host "Created Application event-log source '$EventSource'."
}
catch {
    # Existing sources throw here; that is expected on reinstall/upgrade.
    Write-Host "Application event-log source '$EventSource' already exists or could not be created: $($_.Exception.Message)" -ForegroundColor Yellow
}

$arguments = @(
    "`"$exePath`"",
    '--contentRoot', "`"$publishFull`"",
    '--environment', 'Production',
    '--urls', $Url,
    '--VisionStudio:DataRoot', "`"$dataFull`""
)
if ($AllowInsecureUrl) { $arguments += @('--Security:AllowInsecureRemoteTransport', 'true') }
$binaryPath = $arguments -join ' '

New-Service -Name $ServiceName -BinaryPathName $binaryPath -DisplayName $DisplayName -StartupType Automatic | Out-Null
& sc.exe description $ServiceName "VisionStudio vision workflow host. DataRoot=$dataFull" | Out-Null
# Restart after crash-like exits (60s delay, three attempts per day), then leave it stopped for
# an operator decision: an endless restart loop hides the root cause and spams the event log.
& sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Warning: sc.exe failure configuration failed (exit $LASTEXITCODE). Recovery actions are not configured." -ForegroundColor Yellow
}
# Delayed auto start avoids competing with device drivers/services during boot.
& sc.exe config $ServiceName start= delayed-auto | Out-Null

Write-Host ""
Write-Host "Service '$ServiceName' installed." -ForegroundColor Green
Write-Host "  binary : $exePath"
Write-Host "  content: $publishFull"
Write-Host "  data   : $dataFull"
Write-Host "  logs   : $(Join-Path $dataFull 'logs')"
Write-Host "  health : $($Url.TrimEnd('/'))/healthz"

if (-not $NoStart) {
    try {
        Start-Service -Name $ServiceName
        (Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
        Write-Host "Service started." -ForegroundColor Green
    }
    catch {
        Write-Host "Service failed to start: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "Recent Application log entries ($EventSource):"
        try { Get-EventLog -LogName Application -Source $EventSource -Newest 10 -ErrorAction Stop | Format-Table -AutoSize | Out-String | Write-Host } catch { }
        exit 1
    }
}
else {
    Write-Host "Service not started (-NoStart). Start it with: Start-Service -Name $ServiceName"
}

Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. Put site overrides in '$(Join-Path $dataFull 'site-config/appsettings.site.json')' (optional)."
Write-Host "  2. Verify 'GET $($Url.TrimEnd('/'))/healthz' returns ready=true."
Write-Host "  3. Enable crash dumps: scripts/configure-crash-dumps.ps1"
Write-Host "  4. For upgrades: stop the service, replace '$publishFull', start the service."
Write-Host "     Never point -PublishDir at a directory that also holds production data."