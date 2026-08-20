<#
.SYNOPSIS
    Reliant unified verification script (CI + local).
.DESCRIPTION
    Runs restore / vulnerable-package audit / format check / clean warning-free
    build / unit / architecture / integration tests.
    Enforces a TEST COUNT GATE: every category must match at least the required
    minimum number of tests, otherwise the run FAILS (a filter that matches 0
    tests can never pass). Produces TRX results, a final-e2e log and a
    test-summary.md under artifacts/.
.PARAMETER SkipFormat
    Skip the format check (fast local iteration).
.PARAMETER SkipIntegration
    Skip integration tests (requires Docker).
.PARAMETER ArtifactDir
    Where to write artifacts (default: <repo>/artifacts).
.EXAMPLE
    ./scripts/verify.ps1
    ./scripts/verify.ps1 -SkipFormat
    ./scripts/verify.ps1 -SkipIntegration
#>
param(
    [switch]$SkipFormat,
    [switch]$SkipIntegration,
    [string]$ArtifactDir = ""
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot/.."

if (-not $ArtifactDir) { $ArtifactDir = Join-Path $root "artifacts" }
$resultsDir = Join-Path $ArtifactDir "test-results"
$logsDir = Join-Path $ArtifactDir "logs"
New-Item -ItemType Directory -Force -Path $resultsDir, $logsDir | Out-Null

$solution = Get-ChildItem -Path $root -Include "*.sln","*.slnx" -File | Select-Object -First 1
if (-not $solution) {
    $solution = Get-ChildItem -Path "$root\*" -Include "*.sln","*.slnx" -File | Select-Object -First 1
}
if (-not $solution) {
    Write-Host "No .sln or .slnx file found at $root" -ForegroundColor Red
    exit 1
}

$testProject = Get-ChildItem -Path $root -Recurse -Filter "*.Tests.csproj" -File | Select-Object -First 1
if (-not $testProject) {
    Write-Host "No test project found under $root" -ForegroundColor Red
    exit 1
}

Write-Host "=== Reliant Verification ===" -ForegroundColor Cyan
Write-Host "Solution: $($solution.Name)"
Write-Host "Test project: $($testProject.Name)"
Write-Host "Artifacts: $ArtifactDir"
Write-Host ""

# ------------------------------------------------------------------ #
# 1. Restore
# ------------------------------------------------------------------ #
Write-Host "[1/7] Restore..." -ForegroundColor Yellow
dotnet restore $solution.FullName
if ($LASTEXITCODE -ne 0) { Write-Host "Restore FAILED" -ForegroundColor Red; exit 1 }

# ------------------------------------------------------------------ #
# 2. Vulnerable dependency gate
# ------------------------------------------------------------------ #
Write-Host "[2/7] Vulnerable dependency audit..." -ForegroundColor Yellow
$vulnerabilityReport =
    dotnet list $solution.FullName package --vulnerable --include-transitive 2>&1
$vulnerabilityReport |
    Set-Content -Path (Join-Path $ArtifactDir "vulnerable-packages.txt") -Encoding UTF8
if ($LASTEXITCODE -ne 0 -or
    $vulnerabilityReport -match 'NU190[1-4]|GHSA-[0-9a-z-]+') {
    $vulnerabilityReport | Write-Host
    Write-Host "Vulnerable dependency audit FAILED" -ForegroundColor Red
    exit 1
}

# ------------------------------------------------------------------ #
# 3. Format check
# ------------------------------------------------------------------ #
if (-not $SkipFormat) {
    Write-Host "[3/7] Format check..." -ForegroundColor Yellow
    dotnet format $solution.FullName --verify-no-changes --verbosity quiet
    if ($LASTEXITCODE -ne 0) { Write-Host "Format check FAILED" -ForegroundColor Red; exit 1 }
} else {
    Write-Host "[3/7] Format check skipped" -ForegroundColor DarkGray
}

# ------------------------------------------------------------------ #
# 4. Build
# ------------------------------------------------------------------ #
Write-Host "[4/7] Build..." -ForegroundColor Yellow
dotnet build $solution.FullName --no-restore --configuration Debug `
    --no-incremental --warnaserror
if ($LASTEXITCODE -ne 0) { Write-Host "Build FAILED" -ForegroundColor Red; exit 1 }

# ------------------------------------------------------------------ #
# 5. Test count gate (discover, do not execute)
# ------------------------------------------------------------------ #
function Get-DiscoveredTestCount([string]$Filter) {
    $output = dotnet test $testProject.FullName --no-build --list-tests --filter $Filter 2>&1
    return (($output | Where-Object { $_ -match '^\s+Reliant\.Tests\.' }) | Measure-Object).Count
}

function Assert-TestGate([string]$Name, [string]$Filter, [int]$Minimum) {
    $count = Get-DiscoveredTestCount -Filter $Filter
    Write-Host "[gate] $Name : discovered $count tests (minimum $Minimum)"
    if ($count -lt $Minimum) {
        Write-Host "GATE FAILED: $Name has $count tests, expected at least $Minimum. A category that matches 0 tests must fail." -ForegroundColor Red
        exit 1
    }
    return $count
}

Write-Host "[5/7] Test count gates..." -ForegroundColor Yellow
$unitCount = Assert-TestGate "Unit" "Category=Unit" 1
$archCount = Assert-TestGate "Architecture" "Category=Architecture" 1
$pgCount = Assert-TestGate "PostgreSQL Integration" "Category=Integration&Dependency=PostgreSQL" 1
$localStackCount = Assert-TestGate "LocalStack Integration" "Category=Integration&Dependency=LocalStack" 1
$httpCount = Assert-TestGate "HttpApi Integration" "Category=Integration&Dependency=HttpApi" 1
$e2eCount = Assert-TestGate "WorkerHost E2E" "Category=Integration&Dependency=WorkerHost" 10
$phase4Count = Assert-TestGate "Phase 4" "Category=Phase4" 6

# ------------------------------------------------------------------ #
# 6. Execute tests with TRX logging
# ------------------------------------------------------------------ #
function Invoke-Category([string]$Name, [string]$Filter, [string]$TrxName) {
    Write-Host "[6/7] $Name..." -ForegroundColor Yellow
    dotnet test $testProject.FullName --no-build --configuration Debug `
        --filter $Filter `
        --logger "trx;LogFileName=$TrxName" `
        --results-directory $resultsDir
    if ($LASTEXITCODE -ne 0) { Write-Host "$Name FAILED" -ForegroundColor Red; exit 1 }
}

Invoke-Category "Unit tests" "Category=Unit" "unit.trx"
Invoke-Category "Architecture tests" "Category=Architecture" "architecture.trx"

if (-not $SkipIntegration) {
    Write-Host "[6/7] Integration tests (PostgreSQL + LocalStack + WorkerHost E2E + HttpApi)..." -ForegroundColor Yellow
    dotnet test $testProject.FullName --no-build --configuration Debug `
        --filter "Category=Integration" `
        --logger "trx;LogFileName=integration.trx" `
        --results-directory $resultsDir 2>&1 | Tee-Object -FilePath (Join-Path $logsDir "final-e2e.log")
    if ($LASTEXITCODE -ne 0) { Write-Host "Integration tests FAILED" -ForegroundColor Red; exit 1 }
} else {
    Write-Host "[6/7] Integration tests skipped" -ForegroundColor DarkGray
}

# ------------------------------------------------------------------ #
# 7. test-summary.md
# ------------------------------------------------------------------ #
$integrationTotal = Get-DiscoveredTestCount -Filter "Category=Integration"
$total = $unitCount + $archCount + $integrationTotal

$summary = @"
# Phase 4 Test Summary

Generated by scripts/verify.ps1.

| Category | Filter | Count | Minimum | Status |
|----------|--------|------:|--------:|--------|
| Unit | ``Category=Unit`` | $unitCount | 1 | Pass |
| Architecture | ``Category=Architecture`` | $archCount | 1 | Pass |
| PostgreSQL Integration | ``Category=Integration&Dependency=PostgreSQL`` | $pgCount | 1 | Pass |
| LocalStack Integration | ``Category=Integration&Dependency=LocalStack`` | $localStackCount | 1 | Pass |
| HttpApi Integration | ``Category=Integration&Dependency=HttpApi`` | $httpCount | 1 | Pass |
| WorkerHost E2E | ``Category=Integration&Dependency=WorkerHost`` | $e2eCount | 10 | Pass |
| Phase 4 | ``Category=Phase4`` | $phase4Count | 6 | Pass |
| **Total tests** | | **$total** | | **Pass** |

Result: **PASS** - all categories matched their minimum test counts and all executed suites passed.
"@
Set-Content -Path (Join-Path $ArtifactDir "test-summary.md") -Value $summary -Encoding UTF8
Write-Host ""
Write-Host "[7/7] Wrote test-summary.md to $ArtifactDir" -ForegroundColor Yellow

Write-Host ""
Write-Host "=== All checks passed ===" -ForegroundColor Green
