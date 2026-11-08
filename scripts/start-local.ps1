<#
.SYNOPSIS
  Start published StockSim Web + Worker (each in its own console)
#>

[CmdletBinding()]
param(
    [string]$HttpUrl = "http://localhost:5167"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$webOut = Join-Path $repoRoot "publish\web"
$workerOut = Join-Path $repoRoot "publish\worker"

if (-not (Test-Path (Join-Path $webOut "StockSim.Web.dll"))) {
    throw "publish\web not found. Run .\scripts\deploy-local.ps1 first."
}
if (-not (Test-Path (Join-Path $workerOut "StockSim.Worker.dll"))) {
    throw "publish\worker not found. Run .\scripts\deploy-local.ps1 first."
}

foreach ($name in @("StockSim.Web", "StockSim.Worker")) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Milliseconds 500

$webExe = Join-Path $webOut "StockSim.Web.exe"
$workerExe = Join-Path $workerOut "StockSim.Worker.exe"

$webCmd = if (Test-Path $webExe) {
    "`"$webExe`""
} else {
    "dotnet `"$(Join-Path $webOut 'StockSim.Web.dll')`""
}

$workerCmd = if (Test-Path $workerExe) {
    "`"$workerExe`""
} else {
    "dotnet `"$(Join-Path $workerOut 'StockSim.Worker.dll')`""
}

Write-Host "Starting Worker..." -ForegroundColor Cyan
Start-Process -FilePath "powershell" -WorkingDirectory $workerOut -ArgumentList @(
    "-NoExit", "-Command",
    "`$env:DOTNET_ENVIRONMENT='Production'; `$env:ASPNETCORE_ENVIRONMENT='Production'; $workerCmd"
)

Start-Sleep -Seconds 2

Write-Host "Starting Web ($HttpUrl)..." -ForegroundColor Cyan
Start-Process -FilePath "powershell" -WorkingDirectory $webOut -ArgumentList @(
    "-NoExit", "-Command",
    "`$env:ASPNETCORE_ENVIRONMENT='Production'; `$env:ASPNETCORE_URLS='$HttpUrl'; $webCmd"
)

Start-Sleep -Seconds 2
Start-Process $HttpUrl

Write-Host "Started. Close the two consoles to stop, or run .\scripts\stop-local.ps1" -ForegroundColor Green
