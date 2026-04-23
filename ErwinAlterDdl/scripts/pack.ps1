#requires -Version 7
<#
.SYNOPSIS
    Pack the two NuGet-publishable ErwinAlterDdl projects into artifacts/.

.DESCRIPTION
    Runs dotnet pack against Core and ComInterop only (the other projects
    inherit IsPackable=false from Directory.Build.props). Produces both the
    .nupkg and the .snupkg (portable symbols) per package.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER OutputDir
    Where to drop the packages. Defaults to artifacts/ at the repo root.

.PARAMETER Clean
    Wipe the output dir before packing (default: $true). Set -Clean:$false
    to accumulate pack output across runs.

.EXAMPLE
    .\scripts\pack.ps1

    Clean Release pack; drops nupkgs into artifacts/.

.EXAMPLE
    .\scripts\pack.ps1 -Configuration Debug -Clean:$false

    Pack Debug bits on top of whatever is already in artifacts/.
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $OutputDir = (Join-Path $PSScriptRoot '..\artifacts'),
    [bool]   $Clean = $true
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$OutputDir = (Resolve-Path -LiteralPath (Join-Path $repoRoot 'artifacts') -ErrorAction SilentlyContinue).Path
if (-not $OutputDir) {
    $OutputDir = Join-Path $repoRoot 'artifacts'
}

if ($Clean -and (Test-Path $OutputDir)) {
    Write-Host "Cleaning $OutputDir"
    Remove-Item $OutputDir -Recurse -Force
}

$projects = @(
    (Join-Path $repoRoot 'src/ErwinAlterDdl.Core/ErwinAlterDdl.Core.csproj'),
    (Join-Path $repoRoot 'src/ErwinAlterDdl.ComInterop/ErwinAlterDdl.ComInterop.csproj')
)

foreach ($proj in $projects) {
    Write-Host "Packing $proj"
    & dotnet pack $proj -c $Configuration --nologo -o $OutputDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet pack failed for $proj"
    }
}

Write-Host ''
Write-Host 'Produced packages:'
Get-ChildItem $OutputDir -Filter '*.*nupkg' | ForEach-Object {
    Write-Host ('  {0,-60} {1,8:N0} bytes' -f $_.Name, $_.Length)
}
