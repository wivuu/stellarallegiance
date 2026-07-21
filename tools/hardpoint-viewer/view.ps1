#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Start the interactive GLB hardpoint viewer and open it in your browser.
.DESCRIPTION
    Launches serve.py (next to this script) with Python 3, forwarding any
    extra arguments straight through.
.EXAMPLE
    ./view.ps1
    # library = <repo>/pick-assets
.EXAMPLE
    ./view.ps1 path/to/models
    # library = that folder
.EXAMPLE
    ./view.ps1 --port 8123 --no-open
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ServeArgs
)

$ErrorActionPreference = 'Stop'

$serve = Join-Path $PSScriptRoot 'serve.py'

$python = Get-Command python3 -ErrorAction SilentlyContinue
if (-not $python) {
    $python = Get-Command python -ErrorAction SilentlyContinue
}
if (-not $python) {
    throw "Python 3 is required but was not found on PATH (looked for 'python3' and 'python')."
}

& $python.Source $serve @ServeArgs
exit $LASTEXITCODE
