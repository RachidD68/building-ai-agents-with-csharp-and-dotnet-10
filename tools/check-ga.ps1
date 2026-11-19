<#
.SYNOPSIS
    Proves that a project resolves no prerelease package, directly or transitively.

.DESCRIPTION
    Reads the project's packages.lock.json (written by restore, checked by
    --locked-mode in CI), lists every package it resolves -- Direct, Transitive
    and CentralTransitive -- and exits 1 if any resolved version carries a
    SemVer prerelease label (anything after a '-', such as 2.9.0-beta.1 or
    1.28.0-preview.261106.1). Run from the repository root:

        pwsh tools/check-ga.ps1
        pwsh tools/check-ga.ps1 -LockFile src/Common/AgenticAI.Common.Azure/packages.lock.json

    The default is the GA-only core, src/Common/AgenticAI.Common.
#>
param(
    [string] $LockFile = (Join-Path $PSScriptRoot '..' 'src' 'Common' 'AgenticAI.Common' 'packages.lock.json')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $LockFile)) {
    Write-Error "No lock file at $LockFile. Run: dotnet restore AgenticAI.slnx"
}

$lock = Get-Content $LockFile -Raw | ConvertFrom-Json
$rows = foreach ($framework in $lock.dependencies.PSObject.Properties) {
    foreach ($package in $framework.Value.PSObject.Properties) {
        $version = if ($package.Value.resolved) { $package.Value.resolved } else { '(project)' }
        [pscustomobject]@{
            Framework  = $framework.Name
            Package    = $package.Name
            Type       = $package.Value.type
            Version    = $version
            Prerelease = $version -match '-'
        }
    }
}

$rows | Sort-Object Framework, Package |
    Format-Table Framework, Package, Type, Version, @{ Label = 'Status'; Expression = { if ($_.Prerelease) { 'PRERELEASE' } else { 'GA' } } } -AutoSize |
    Out-String -Width 200 | Write-Output

$bad = @($rows | Where-Object Prerelease)
$name = Split-Path (Split-Path $LockFile -Parent) -Leaf
if ($bad.Count -gt 0) {
    Write-Output "FAIL: $name resolves $($bad.Count) prerelease package(s) out of $($rows.Count): $(($bad | ForEach-Object { "$($_.Package) $($_.Version)" }) -join ', ')"
    exit 1
}
Write-Output "PASS: $name resolves $($rows.Count) package(s), all GA (no prerelease version, direct or transitive)."
exit 0
