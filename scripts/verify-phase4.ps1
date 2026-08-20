<#
.SYNOPSIS
    Validate the local Phase 4 observability implementation.
.DESCRIPTION
    Validates Docker Compose and dashboard JSON, builds Release, runs the
    Phase 4 test suite, and optionally checks a running observability stack.
#>
param(
    [switch]$CheckRunningStack
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot/.."
$testProject = Join-Path $root "tests/Reliant.Tests/Reliant.Tests.csproj"
$composeFile = Join-Path $root "docker-compose.observability.yml"
$dashboard = Join-Path $root "ops/observability/grafana/dashboards/reliant-operations.json"

Write-Host "[1/4] Validate observability configuration" -ForegroundColor Yellow
docker compose -f $composeFile config --quiet
if ($LASTEXITCODE -ne 0) { throw "Observability Compose validation failed" }
Get-Content $dashboard -Raw | ConvertFrom-Json | Out-Null

Write-Host "[2/4] Build Release" -ForegroundColor Yellow
dotnet build (Join-Path $root "Reliant.slnx") -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Release build failed" }

Write-Host "[3/4] Run Phase 4 tests" -ForegroundColor Yellow
dotnet test $testProject -c Release --no-build `
    --filter "FullyQualifiedName~Reliant.Tests.Integration.Phase4" `
    --logger "console;verbosity=normal"
if ($LASTEXITCODE -ne 0) { throw "Phase 4 tests failed" }

if ($CheckRunningStack) {
    Write-Host "[4/4] Check running stack" -ForegroundColor Yellow
    $checks = @(
        @{ Name = "Collector"; Uri = "http://localhost:13133/" },
        @{ Name = "Prometheus"; Uri = "http://localhost:9090/-/ready" },
        @{ Name = "Tempo"; Uri = "http://localhost:3200/ready" },
        @{ Name = "Loki"; Uri = "http://localhost:3100/ready" },
        @{ Name = "Grafana"; Uri = "http://localhost:3000/api/health" }
    )
    foreach ($check in $checks) {
        $response = Invoke-WebRequest -UseBasicParsing `
            -Uri $check.Uri -TimeoutSec 5
        if ($response.StatusCode -ne 200) {
            throw "$($check.Name) health check failed"
        }
        Write-Host "  $($check.Name): 200"
    }
} else {
    Write-Host "[4/4] Running stack check skipped" -ForegroundColor DarkGray
}

Write-Host "Phase 4 verification PASS" -ForegroundColor Green

