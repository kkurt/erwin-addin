# Elite Soft Erwin Add-In - DDLGENERATOR flavor dev build wrapper.
#
# Thin wrapper around build-and-run.ps1 -DdlGenerator: builds the add-in with
# the DDLGENERATOR symbol (dedicated, always-on DDL queue worker: no
# validation surfaces, General tab only), installs and COM-registers it
# exactly like the normal dev flow.
#
# WARNING: both flavors share the same COM CLSID - installing this flavor
# REPLACES the normal add-in on this machine. Run plain .\build-and-run.ps1
# to switch back to the interactive flavor.
#
# Usage:
#   .\build-and-run-ddlgenerator.ps1                       Build + install DDL-generator flavor
#   .\build-and-run-ddlgenerator.ps1 -KillAllErwinProcs    Same, also kill other users' erwin

param(
    [switch]$KillAllErwinProcs
)

& (Join-Path $PSScriptRoot 'build-and-run.ps1') -DdlGenerator -KillAllErwinProcs:$KillAllErwinProcs
exit $LASTEXITCODE
