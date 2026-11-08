<#
.SYNOPSIS
  StockSim local one-click publish (Windows / PowerShell)

.DESCRIPTION
  - Check .NET SDK
  - Publish Web + Worker to publish\web / publish\worker
  - Write absolute-path appsettings (shared SQLite, repo data/)
  - Copy Alerts (Enabled / Webhook / PushPlus) from src\StockSim.Web\appsettings.json to Web+Worker
  - If stocksim.db exists: keep or reset (or -KeepDatabase / -ResetDatabase)
  - Optional: start both processes

.EXAMPLE
  .\scripts\deploy-local.ps1
  .\scripts\deploy-local.ps1 -Start
  .\scripts\deploy-local.ps1 -KeepDatabase -Start
  .\scripts\deploy-local.ps1 -ResetDatabase -Start
  .\scripts\deploy-local.ps1 -SkipBuild -Start
#>

[CmdletBinding()]
param(
    [switch]$Start,
    [switch]$SkipBuild,
    [switch]$KeepDatabase,
    [switch]$ResetDatabase,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$HttpUrl = "http://localhost:5167"
)

$ErrorActionPreference = "Stop"

if ($KeepDatabase -and $ResetDatabase) {
    throw "Cannot use -KeepDatabase and -ResetDatabase together."
}

function Get-RepoRoot {
    $dir = Split-Path -Parent $PSScriptRoot
    $hasSlnx = Test-Path (Join-Path $dir "StockSim.slnx")
    $hasDocs = Test-Path (Join-Path $dir "docs")
    if (-not $hasSlnx -and -not $hasDocs) {
        throw "Cannot find repo root. Run this script from inside the repository."
    }
    return $dir
}

function Assert-DotNet {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        throw "dotnet not found. Install .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0"
    }
    $ver = (& dotnet --version).Trim()
    Write-Host "[OK] .NET SDK $ver" -ForegroundColor Green
}

function Stop-StockSimProcesses {
    foreach ($name in @("StockSim.Web", "StockSim.Worker")) {
        Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
            Write-Host "Stopping $($_.ProcessName) (PID $($_.Id))..." -ForegroundColor Yellow
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        }
    }
    Start-Sleep -Seconds 1
}

function Write-AppSettings {
    param(
        [string]$Path,
        [hashtable]$Object
    )
    $json = $Object | ConvertTo-Json -Depth 8
    # UTF-8 without BOM can confuse some tools; use UTF8 for appsettings
    [System.IO.File]::WriteAllText($Path, $json, [System.Text.UTF8Encoding]::new($false))
    Write-Host "Wrote config: $Path" -ForegroundColor Cyan
}

function Read-AlertsBlock {
    param([string]$SourcePath)

    $defaults = @{
        Enabled         = $false
        DedupMinutes    = 3
        TimeoutSeconds  = 3
        WebhookUrl      = ""
        WebhookFormat   = "text"
        PushPlusToken   = ""
        PushPlusTopic   = ""
        TitlePrefix     = "StockSim"
    }

    if (-not (Test-Path -LiteralPath $SourcePath)) {
        Write-Host "Alerts source missing: $SourcePath (using defaults)" -ForegroundColor Yellow
        return $defaults
    }

    try {
        $json = Get-Content -LiteralPath $SourcePath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($null -eq $json.Alerts) {
            Write-Host "No Alerts section in $SourcePath (using defaults)" -ForegroundColor Yellow
            return $defaults
        }

        $a = $json.Alerts
        $block = @{
            Enabled         = [bool]($a.Enabled)
            DedupMinutes    = if ($null -ne $a.DedupMinutes) { [int]$a.DedupMinutes } else { 3 }
            TimeoutSeconds  = if ($null -ne $a.TimeoutSeconds) { [int]$a.TimeoutSeconds } else { 3 }
            WebhookUrl      = if ($null -ne $a.WebhookUrl) { [string]$a.WebhookUrl } else { "" }
            WebhookFormat   = if ($a.WebhookFormat) { [string]$a.WebhookFormat } else { "text" }
            PushPlusToken   = if ($null -ne $a.PushPlusToken) { [string]$a.PushPlusToken } else { "" }
            PushPlusTopic   = if ($null -ne $a.PushPlusTopic) { [string]$a.PushPlusTopic } else { "" }
            TitlePrefix     = if ($a.TitlePrefix) { [string]$a.TitlePrefix } else { "StockSim" }
        }

        $channels = @()
        if (-not [string]::IsNullOrWhiteSpace($block.WebhookUrl)) { $channels += "Webhook" }
        if (-not [string]::IsNullOrWhiteSpace($block.PushPlusToken)) { $channels += "PushPlus" }
        $ch = if ($channels.Count -gt 0) { $channels -join "+" } else { "none" }
        Write-Host ("Alerts from source: Enabled={0}, channels={1}" -f $block.Enabled, $ch) -ForegroundColor Cyan
        return $block
    }
    catch {
        Write-Host "Failed to read Alerts from $SourcePath; using defaults. $_" -ForegroundColor Yellow
        return $defaults
    }
}

function Resolve-DatabaseChoice {
    param(
        [bool]$HadExistingDb,
        [switch]$KeepDatabase,
        [switch]$ResetDatabase
    )

    if (-not $HadExistingDb) {
        return "new"
    }

    if ($KeepDatabase) { return "keep" }
    if ($ResetDatabase) { return "reset" }

    Write-Host ""
    Write-Host "Existing database found: publish\web\stocksim.db" -ForegroundColor Yellow
    Write-Host "  [K] Keep data (restore db after publish)" -ForegroundColor Cyan
    Write-Host "  [R] Reset (delete db; recreate on startup)" -ForegroundColor Cyan
    $ans = Read-Host "Choose [K/R] (Enter = Keep)"
    if ([string]::IsNullOrWhiteSpace($ans) -or $ans -match '^[Kk]') {
        return "keep"
    }
    if ($ans -match '^[Rr]') {
        return "reset"
    }

    Write-Host "Invalid input; keeping database." -ForegroundColor Yellow
    return "keep"
}

$repoRoot = Get-RepoRoot
Set-Location $repoRoot
Write-Host "Repo root: $repoRoot" -ForegroundColor Cyan

Assert-DotNet
Stop-StockSimProcesses

$publishRoot = Join-Path $repoRoot "publish"
$webOut = Join-Path $publishRoot "web"
$workerOut = Join-Path $publishRoot "worker"
$dataReplay = Join-Path $repoRoot "data\replay"
$dataAi = Join-Path $repoRoot "data\ai"
$dbPath = Join-Path $webOut "stocksim.db"
$dbBackup = Join-Path $publishRoot "stocksim.db.bak"

New-Item -ItemType Directory -Force -Path $publishRoot, $dataReplay, $dataAi | Out-Null

$hadExistingDb = Test-Path $dbPath
$dbChoice = Resolve-DatabaseChoice -HadExistingDb $hadExistingDb -KeepDatabase:$KeepDatabase -ResetDatabase:$ResetDatabase

if (-not $SkipBuild) {
    if ($hadExistingDb) {
        Write-Host "Backing up DB to: $dbBackup" -ForegroundColor Cyan
        Copy-Item -Path $dbPath -Destination $dbBackup -Force
    }

    Write-Host "`n=== restore ===" -ForegroundColor Cyan
    dotnet restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }

    Write-Host "`n=== publish Web ($Configuration) ===" -ForegroundColor Cyan
    if (Test-Path $webOut) { Remove-Item $webOut -Recurse -Force }
    dotnet publish "src\StockSim.Web\StockSim.Web.csproj" -c $Configuration -o $webOut --no-self-contained
    if ($LASTEXITCODE -ne 0) { throw "Web publish failed" }

    Write-Host "`n=== publish Worker ($Configuration) ===" -ForegroundColor Cyan
    if (Test-Path $workerOut) { Remove-Item $workerOut -Recurse -Force }
    dotnet publish "src\StockSim.Worker\StockSim.Worker.csproj" -c $Configuration -o $workerOut --no-self-contained
    if ($LASTEXITCODE -ne 0) { throw "Worker publish failed" }

    if ($dbChoice -eq "keep" -and (Test-Path $dbBackup)) {
        Copy-Item -Path $dbBackup -Destination $dbPath -Force
        Write-Host "Restored database: $dbPath" -ForegroundColor Green
    }
    elseif ($dbChoice -eq "reset" -and $hadExistingDb) {
        Write-Host "Reset selected: old DB not restored; will create on startup." -ForegroundColor Yellow
    }
    elseif (-not $hadExistingDb) {
        Write-Host "No previous DB: stocksim.db will be created on first start." -ForegroundColor Cyan
    }
}
else {
    if (-not (Test-Path (Join-Path $webOut "StockSim.Web.dll"))) {
        throw "Published Web not found. Run without -SkipBuild first."
    }
    if (-not (Test-Path (Join-Path $workerOut "StockSim.Worker.dll"))) {
        throw "Published Worker not found. Run without -SkipBuild first."
    }
    Write-Host "SkipBuild: using existing publish/ (DB unchanged)." -ForegroundColor Yellow
}

# Alerts：以 Web 源配置为准，同步写入 Web + Worker 发布目录（成交通知主要靠 Worker）
$alertsSource = Join-Path $repoRoot "src\StockSim.Web\appsettings.json"
$alertsBlock = Read-AlertsBlock -SourcePath $alertsSource

$cursorBlock = @{
    Enabled                 = $true
    Model                   = "composer-2.5"
    TimeoutSeconds          = 180
    FallbackToPlaceholder   = $false
    BridgeDirectory         = (Join-Path $repoRoot "adapters\cursor-agent-bridge")
    NodeExecutable          = (Join-Path $repoRoot "tools\node\node.exe")
    ApiKey                  = ""
}

# Web config (absolute paths)
$webSettings = @{
    ConnectionStrings = @{
        StockSim = "Data Source=$dbPath"
    }
    Database = @{
        Provider = "Sqlite"
    }
    MarketData = @{
        ReplayDirectory = $dataReplay
        Provider        = "EastMoney"
    }
    AI = @{
        WorkDirectory = $dataAi
        Cursor        = $cursorBlock
    }
    Alerts = $alertsBlock
    Logging = @{
        LogLevel = @{
            Default                = "Information"
            "Microsoft.AspNetCore" = "Warning"
        }
    }
    AllowedHosts = "*"
    Urls         = $HttpUrl
}
Write-AppSettings -Path (Join-Path $webOut "appsettings.json") -Object $webSettings
Write-AppSettings -Path (Join-Path $webOut "appsettings.Production.json") -Object $webSettings

# Worker config (same stocksim.db)
$workerSettings = @{
    ConnectionStrings = @{
        StockSim = "Data Source=$dbPath"
    }
    Database = @{
        Provider = "Sqlite"
    }
    MarketData = @{
        ReplayDirectory = $dataReplay
    }
    AI = @{
        WorkDirectory = $dataAi
        Cursor        = $cursorBlock
    }
    Alerts = $alertsBlock
    Worker = @{
        StrategyIntervalSeconds      = 3
        NightlyStrategyRefreshHour   = 22
        NightlyStrategyRefreshMinute = 0
    }
    Logging = @{
        LogLevel = @{
            Default                   = "Information"
            "Microsoft.Hosting.Lifetime" = "Information"
        }
    }
}
Write-AppSettings -Path (Join-Path $workerOut "appsettings.json") -Object $workerSettings
Write-AppSettings -Path (Join-Path $workerOut "appsettings.Production.json") -Object $workerSettings

$readme = @"
StockSim local publish
======================
Web:    $webOut
Worker: $workerOut
SQLite: $dbPath
Replay: $dataReplay
AI:     $dataAi
URL:    $HttpUrl

Start:  .\scripts\start-local.ps1
Stop:   .\scripts\stop-local.ps1

DB: on full republish choose Keep(K) or Reset(R); or -KeepDatabase / -ResetDatabase.
Alerts: copied from src\StockSim.Web\appsettings.json into publish\web and publish\worker.
Site is open (no login).
"@
[System.IO.File]::WriteAllText((Join-Path $publishRoot "README.txt"), $readme, [System.Text.UTF8Encoding]::new($false))

Write-Host "`n=== Publish done ===" -ForegroundColor Green
Write-Host "Web:    $webOut"
Write-Host "Worker: $workerOut"
Write-Host "DB:     $dbPath"
if (-not $SkipBuild) {
    $policyLabel = if ($dbChoice -eq "keep") { "keep" } elseif ($dbChoice -eq "reset") { "reset" } else { "new" }
    Write-Host "DB policy: $policyLabel"
}
Write-Host "URL:     $HttpUrl"

if ($Start) {
    Write-Host "`n=== Starting services ===" -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot "start-local.ps1") -HttpUrl $HttpUrl
}
else {
    Write-Host "`nNot started. Run:" -ForegroundColor Yellow
    Write-Host "  .\scripts\start-local.ps1"
    Write-Host "or:"
    Write-Host "  .\scripts\deploy-local.ps1 -Start"
}
