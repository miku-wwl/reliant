<#
.SYNOPSIS
    Reliant unified verification script.
.DESCRIPTION
    Single entry point for both local development and CI.
    Runs: restore / format check / build / unit tests / architecture tests / migration tests.
.PARAMETER SkipFormat
    Skip format check (useful for quick local iteration).
.PARAMETER SkipIntegration
    Skip integration tests (requires Docker).
.EXAMPLE
    ./scripts/verify.ps1
    ./scripts/verify.ps1 -SkipFormat
    ./scripts/verify.ps1 -SkipIntegration
#>
param(
    [switch]$SkipFormat,
    [switch]$SkipIntegration
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot/.."

$solution = Get-ChildItem -Path $root -Include "*.sln","*.slnx" -File | Select-Object -First 1
if (-not $solution) {
    $solution = Get-ChildItem -Path "$root\*" -Include "*.sln","*.slnx" -File | Select-Object -First 1
}
if (-not $solution) {
    Write-Host "No .sln or .slnx file found at $root" -ForegroundColor Red
    Write-Host "This is expected during Phase 0 Stage A. CI will pass with a warning." -ForegroundColor Yellow
    exit 0
}

Write-Host "=== Reliant Verification ===" -ForegroundColor Cyan
Write-Host "Solution: $($solution.Name)"
Write-Host ""

# 1. Restore
Write-Host "[1/6] Restore..." -ForegroundColor Yellow
dotnet restore $solution.FullName
if ($LASTEXITCODE -ne 0) { Write-Host "Restore FAILED" -ForegroundColor Red; exit 1 }

# 2. Format check
if (-not $SkipFormat) {
    Write-Host "[2/6] Format check..." -ForegroundColor Yellow
    dotnet format $solution.FullName --verify-no-changes --verbosity quiet
    if ($LASTEXITCODE -ne 0) { Write-Host "Format check FAILED" -ForegroundColor Red; exit 1 }
} else {
    Write-Host "[2/6] Format check skipped" -ForegroundColor DarkGray
}

# 3. Build
Write-Host "[3/6] Build..." -ForegroundColor Yellow
dotnet build $solution.FullName --no-restore --configuration Debug
if ($LASTEXITCODE -ne 0) { Write-Host "Build FAILED" -ForegroundColor Red; exit 1 }

# 4. Unit tests
Write-Host "[4/6] Unit tests..." -ForegroundColor Yellow
dotnet test $solution.FullName --no-build --configuration Debug --filter "Category=Unit" --logger "console;verbosity=normal"
if ($LASTEXITCODE -ne 0) { Write-Host "Unit tests FAILED" -ForegroundColor Red; exit 1 }

# 5. Architecture tests
Write-Host "[5/6] Architecture tests..." -ForegroundColor Yellow
dotnet test $solution.FullName --no-build --configuration Debug --filter "Category=Architecture" --logger "console;verbosity=normal"
if ($LASTEXITCODE -ne 0) { Write-Host "Architecture tests FAILED" -ForegroundColor Red; exit 1 }

# 6. Integration tests
if (-not $SkipIntegration) {
    Write-Host "[6/6] Integration tests..." -ForegroundColor Yellow
    dotnet test $solution.FullName --no-build --configuration Debug --filter "Category=Integration" --logger "console;verbosity=normal"
    if ($LASTEXITCODE -ne 0) { Write-Host "Integration tests FAILED" -ForegroundColor Red; exit 1 }
} else {
    Write-Host "[6/6] Integration tests skipped" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "=== All checks passed ===" -ForegroundColor Green
