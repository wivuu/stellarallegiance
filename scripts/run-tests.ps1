#!/usr/bin/env pwsh
#Requires -Version 7.3
# Run the tests/ suites locally and print a pass/fail table. There is NO CI in this repo — this
# script is the "run everything" entry point, and running it is a human decision.
#
# Every suite under tests/ is a console app (`dotnet run`) that prints its own PASS/FAIL lines and
# exits non-zero when anything failed; this script just sequences them and aggregates the exit
# codes. Suites are built in Release, one at a time, in alphabetical order.
#
#   scripts/run-tests.ps1                       # every suite except PublicLobbyTest
#   scripts/run-tests.ps1 -Filter Collision     # only suites whose name contains "Collision"
#   scripts/run-tests.ps1 -Filter Fog,Mine      # several substrings
#   scripts/run-tests.ps1 -IncludeDocker        # also PublicLobbyTest (needs a running Docker)
#
# tests/PublicLobbyTest is skipped by default: its schema/grain sections stand up a Testcontainers
# Postgres, so it needs Docker reachable. A skipped suite is not a failure.
#
# Exit code: 0 when every suite that ran passed, 1 otherwise.
param(
    [string[]]$Filter,
    [switch]$IncludeDocker
)

$ErrorActionPreference = 'Stop'
# The OPPOSITE of run-server.ps1: a failing suite must return its exit code for the summary
# table, not throw and abort the whole run.
$PSNativeCommandUseErrorActionPreference = $false

$RepoRoot = Split-Path $PSScriptRoot -Parent
$TestsDir = Join-Path $RepoRoot 'tests'

# Needs Docker (Testcontainers Postgres) — opt in with -IncludeDocker.
$DockerSuites = @('PublicLobbyTest')

# Class libraries the suites reference (no Main to run) — not suites.
$HelperProjects = @('TestKit')

# A suite is a tests/<Name>/ directory holding <Name>.csproj.
$suites = Get-ChildItem $TestsDir -Directory | Sort-Object Name | Where-Object {
    (Test-Path (Join-Path $_.FullName "$($_.Name).csproj")) -and ($HelperProjects -notcontains $_.Name)
}

if ($Filter) {
    $suites = $suites | Where-Object {
        $name = $_.Name
        @($Filter | Where-Object { $name -like "*$_*" }).Count -gt 0
    }
    if (-not $suites) {
        Write-Host "[run-tests] no suite matches -Filter $($Filter -join ',')"
        exit 1
    }
}

Set-Location $RepoRoot
$results = [System.Collections.Generic.List[object]]::new()

foreach ($suite in $suites) {
    $name = $suite.Name
    $project = "tests/$name"

    if ($DockerSuites -contains $name -and -not $IncludeDocker) {
        Write-Host "[run-tests] SKIP $name (needs Docker; pass -IncludeDocker to run it)"
        $results.Add([pscustomobject]@{ Suite = $name; Result = 'SKIP'; Seconds = 0.0 })
        continue
    }

    Write-Host ''
    Write-Host "===== $name =====" -ForegroundColor Cyan
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    & dotnet run --project $project -c Release
    $code = $LASTEXITCODE
    $sw.Stop()

    $results.Add([pscustomobject]@{
        Suite   = $name
        Result  = if ($code -eq 0) { 'PASS' } else { "FAIL ($code)" }
        Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    })
}

Write-Host ''
Write-Host '===== summary =====' -ForegroundColor Cyan
$results | Format-Table -AutoSize | Out-String | Write-Host

$failed = @($results | Where-Object { $_.Result -like 'FAIL*' })
$skipped = @($results | Where-Object { $_.Result -eq 'SKIP' })
$passed = @($results | Where-Object { $_.Result -eq 'PASS' })
Write-Host "[run-tests] $($passed.Count) passed, $($failed.Count) failed, $($skipped.Count) skipped"

if ($failed.Count -gt 0) {
    Write-Host "[run-tests] failed: $(($failed.Suite) -join ', ')" -ForegroundColor Red
    exit 1
}
exit 0
