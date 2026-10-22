# Shared publish-path safety helpers (Q03, 2026-10-13 review).
# Dot-sourced by publish-runtime.ps1 and by scripts/test-publish-data-separation.ps1 so the
# guard and its regression suite can never drift apart.
# ASCII-only: Windows PowerShell 5.1 parses BOM-less UTF-8 as ANSI and silently eats lines.

<#
Resolve reparse points (junctions / symlinks) to the final physical path. Non-existent paths
fall back to the normalized lexical path, so pre-flight checks work before anything is created.
A bounded loop guards against link cycles.
#>
function Resolve-RealPath([string]$path) {
    $current = [System.IO.Path]::GetFullPath($path)
    for ($i = 0; $i -lt 32; $i++) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($item -and $item.LinkType) {
            $target = $item.Target
            if ($target -is [array]) { $target = $target[0] }
            if ($target) {
                if (-not [System.IO.Path]::IsPathRooted([string]$target)) {
                    $target = Join-Path (Split-Path -Parent $current) ([string]$target)
                }
                $current = [System.IO.Path]::GetFullPath([string]$target)
                continue
            }
        }
        break
    }
    return $current
}

<#
True when the two paths are identical or one contains the other (either direction), compared
on their resolved physical paths. This is the bidirectional containment test the publish
pre-flight needs: the data root must never be the output, inside it, an ancestor of it, or
overlap any rollback/staging path (those get renamed, moved or deleted during the swap).
#>
function Test-PathOverlaps([string]$left, [string]$right) {
    $a = (Resolve-RealPath $left).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $b = (Resolve-RealPath $right).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    if ($a -eq $b) { return $true }
    $sep = [System.IO.Path]::DirectorySeparatorChar
    return $a.StartsWith($b + $sep, [System.StringComparison]::OrdinalIgnoreCase) -or
           $b.StartsWith($a + $sep, [System.StringComparison]::OrdinalIgnoreCase)
}

<#
Q03 pre-flight: reject every overlap between the production data root and the package
lifecycle paths (output, staging, previous). The publish ends with
'Remove-Item previous -Recurse -Force', so a data root pointing at (or inside) previous
would delete the live database and its upgrade checkpoints. Returns $true when safe;
prints the reason and returns $false otherwise.
#>
function Test-PublishPathsSeparated([string]$Output, [string]$Staging, [string]$Previous, [string]$DataRoot) {
    foreach ($lifecycle in @($Output, $Staging, $Previous)) {
        if (Test-PathOverlaps $DataRoot $lifecycle) {
            Write-Host "DataRoot must not overlap the package lifecycle path '$lifecycle' (resolved: '$(Resolve-RealPath $lifecycle)')." -ForegroundColor Red
            Write-Host "The publish swaps and deletes package directories; a data root at '$DataRoot' would be renamed or deleted with them." -ForegroundColor Red
            return $false
        }
    }
    return $true
}
