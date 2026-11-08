<#
.SYNOPSIS
  Stop local StockSim Web / Worker processes
#>

$ErrorActionPreference = "Continue"

$stopped = $false
foreach ($name in @("StockSim.Web", "StockSim.Worker")) {
    $procs = Get-Process -Name $name -ErrorAction SilentlyContinue
    foreach ($p in $procs) {
        Write-Host "Stopping $($p.ProcessName) (PID $($p.Id))" -ForegroundColor Yellow
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        $stopped = $true
    }
}

if (-not $stopped) {
    Write-Host "No running StockSim.Web / StockSim.Worker." -ForegroundColor DarkGray
}
else {
    Write-Host "Stopped." -ForegroundColor Green
}
