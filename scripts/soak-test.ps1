$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Native commands do not fail the script through ErrorActionPreference: check exit codes explicitly.
function Assert-LastExitCode([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Soak gate step failed: $step (exit code $LASTEXITCODE)." -ForegroundColor Red
        exit 1
    }
}

Write-Host "== VisionStudio V0.36 Soak Gate =="
Push-Location "$root/backend"
try {
    dotnet restore VisionStudio.slnx
    Assert-LastExitCode 'dotnet restore'

    dotnet test VisionStudio.slnx --configuration Release --no-restore --filter "Category=Soak"
    Assert-LastExitCode 'soak tests'
}
finally { Pop-Location }
Write-Host "Soak Gate PASSED"
