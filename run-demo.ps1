# AutoPay Rewrite POC — one-command demo.
# Builds everything, starts Externals (WireMock) + BillingCore in their own windows,
# waits for readiness, then runs the D1-D9 scenario suite in this window.

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# Prefer a user-local SDK install if the machine has none on PATH.
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -or -not (dotnet --list-sdks 2>$null)) {
    $env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
    $env:PATH = "$env:USERPROFILE\.dotnet;$env:PATH"
}

Write-Host "Building..." -ForegroundColor Cyan
dotnet build "$root\AutopayPoc.sln" -v quiet
if ($LASTEXITCODE -ne 0) { throw "build failed" }

# Postgres: docker compose if the engine is up, otherwise BillingCore starts embedded Postgres itself.
$dockerUp = $false
try { docker info *> $null; $dockerUp = ($LASTEXITCODE -eq 0) } catch { }
if ($dockerUp) {
    Write-Host "Docker detected - starting compose Postgres on :5433" -ForegroundColor Cyan
    docker compose -f "$root\docker-compose.yml" up -d
} else {
    Write-Host "Docker not available - BillingCore will start embedded PostgreSQL on :5433" -ForegroundColor Yellow
}

Write-Host "Starting Externals (WireMock stubs) on :9876..." -ForegroundColor Cyan
Start-Process powershell -ArgumentList "-NoExit", "-Command",
    "`$env:DOTNET_ROOT='$env:DOTNET_ROOT'; `$env:PATH='$env:PATH'; dotnet run --project '$root\src\Externals' --no-build"

Write-Host "Starting BillingCore on :5080..." -ForegroundColor Cyan
Start-Process powershell -ArgumentList "-NoExit", "-Command",
    "`$env:DOTNET_ROOT='$env:DOTNET_ROOT'; `$env:PATH='$env:PATH'; dotnet run --project '$root\src\BillingCore' --no-build"

Write-Host "Waiting for BillingCore (embedded Postgres may download binaries on first run)..." -ForegroundColor Cyan
$deadline = (Get-Date).AddMinutes(5)
while ((Get-Date) -lt $deadline) {
    try {
        Invoke-RestMethod "http://localhost:5080/demo/time" -TimeoutSec 2 | Out-Null
        break
    } catch { Start-Sleep -Seconds 2 }
}

Write-Host "Running scenarios D1-D9..." -ForegroundColor Cyan
dotnet run --project "$root\src\DemoRunner" --no-build
Write-Host ""
Write-Host "Explore live: Swagger http://localhost:5080/swagger · WireMock journals http://localhost:9876/admin/journal/charges" -ForegroundColor Green
