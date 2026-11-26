[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ResultsRoot
)

# 失败用例可读性：CI 上完整回归失败时，日志默认只剩 "Process completed with exit code 1"。
# dotnet test 用 --logger trx 产出 TRX，这里把 Failed 的用例名与错误首行打到日志里，
# 让下一次失败能在 checks 页面直接定位，而不用重跑整条流水线。
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ResultsRoot)) {
    Write-Host "No test results directory at $ResultsRoot."
    exit 0
}

$files = @(Get-ChildItem -LiteralPath $ResultsRoot -Recurse -Filter *.trx -File)
if ($files.Count -eq 0) {
    Write-Host "No TRX files under $ResultsRoot; the test host likely failed before writing results."
    exit 0
}

$namespace = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
$failedCount = 0

foreach ($file in $files) {
    [xml] $trx = Get-Content -LiteralPath $file.FullName -Raw
    $failed = @(Select-Xml -Xml $trx -XPath '//t:UnitTestResult[@outcome="Failed"]' -Namespace $namespace)
    if ($failed.Count -eq 0) {
        continue
    }

    Write-Host "Failed tests in $($file.Name):"
    foreach ($match in $failed) {
        $failedCount++
        $name = $match.Node.testName
        $message = $match.Node.Output.ErrorInfo.Message
        if ([string]::IsNullOrWhiteSpace($message)) {
            Write-Host "  - $name"
        }
        else {
            $firstLine = ($message -split "`r?`n")[0].Trim()
            Write-Host "  - $name :: $firstLine"
        }
    }
}

if ($failedCount -eq 0) {
    Write-Host "TRX files contain no failed tests; the failure came from the build or the test host itself."
}
else {
    Write-Host "$failedCount failed test(s) reported above."
}
