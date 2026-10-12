# PowerShell 5.1 / .NET compatibility: when the host process is started from Git Bash
# (msys) the inherited process environment can contain the same variable under two
# spellings (e.g. HTTP_PROXY and http_proxy). Start-Process then fails while building
# the child environment block ("an item with the same key has already been added").
#
# Remove-DuplicateProcessEnvironment collapses every case-duplicate group into a single
# canonical (upper-case) entry before child processes are spawned. Values are preserved;
# Windows environment variable lookups are case-insensitive, so behaviour is unchanged.
function Remove-DuplicateProcessEnvironment {
    $variables = [Environment]::GetEnvironmentVariables('Process')
    $duplicates = $variables.Keys |
        Group-Object { $_.ToUpperInvariant() } |
        Where-Object { $_.Count -gt 1 }
    foreach ($group in $duplicates) {
        $canonicalName = $group.Name
        $value = [Environment]::GetEnvironmentVariable($canonicalName, 'Process')
        # SetEnvironmentVariable(name, $null) removes one case-insensitive match per call;
        # repeated calls (one per duplicate entry) clear the group regardless of which
        # spelling the runtime finds first, then the canonical entry is re-created.
        foreach ($spelling in $group.Group) {
            [Environment]::SetEnvironmentVariable($spelling, $null, 'Process')
        }
        [Environment]::SetEnvironmentVariable($canonicalName, $value, 'Process')
    }
}
