$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Write-Host "== VisionStudio V0.36 Soak Gate =="
Push-Location "$root/backend"
try {
    dotnet restore VisionStudio.slnx
    dotnet test VisionStudio.slnx --configuration Release --no-restore --filter "Category=Soak"
}
finally { Pop-Location }
Write-Host "Soak Gate PASSED"
