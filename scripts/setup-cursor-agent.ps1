<#
.SYNOPSIS
  Install StockSim Cursor Agent SDK bridge (portable Node + npm deps), optional browser login.

.DESCRIPTION
  Cursor SDK auth options:
  1) -Login: browser login for current Cursor account, stores local SDK credentials
  2) env CURSOR_API_KEY (Dashboard -> Integrations)
  3) appsettings AI:Cursor:ApiKey (do not commit)

.EXAMPLE
  .\scripts\setup-cursor-agent.ps1
  .\scripts\setup-cursor-agent.ps1 -Login
#>

[CmdletBinding()]
param(
    [switch]$Login,
    [switch]$SkipDownloadNode
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$bridgeDir = Join-Path $repoRoot "adapters\cursor-agent-bridge"
$toolsNode = Join-Path $repoRoot "tools\node"
$nodeVersion = "v22.23.4"
$zipName = "node-$nodeVersion-win-x64.zip"
$zipUrl = "https://nodejs.org/download/release/$nodeVersion/$zipName"

function Write-Step([string]$msg) {
    Write-Host ""
    Write-Host ("=== {0} ===" -f $msg) -ForegroundColor Cyan
}

function Find-SystemNode {
    $cmd = Get-Command node -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Ensure-PortableNode {
    $nodeExe = Join-Path $toolsNode "node.exe"
    $npmCmd = Join-Path $toolsNode "npm.cmd"
    if ((Test-Path $nodeExe) -and (Test-Path $npmCmd)) {
        Write-Host ("Portable Node ready: {0}" -f $nodeExe) -ForegroundColor Green
        return @{ Node = $nodeExe; Npm = $npmCmd }
    }

    if ($SkipDownloadNode) {
        throw "tools\node is missing while -SkipDownloadNode was specified"
    }

    Write-Step ("Download portable Node.js {0}" -f $nodeVersion)
    New-Item -ItemType Directory -Force -Path (Join-Path $repoRoot "tools") | Out-Null
    $zipPath = Join-Path $repoRoot ("tools\{0}" -f $zipName)
    Write-Host ("Downloading {0} ..." -f $zipUrl)
    Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing

    $extractTo = Join-Path $repoRoot "tools\_node_extract"
    if (Test-Path $extractTo) { Remove-Item $extractTo -Recurse -Force }
    Expand-Archive -Path $zipPath -DestinationPath $extractTo -Force

    $extracted = Get-ChildItem $extractTo -Directory | Select-Object -First 1
    if (-not $extracted) { throw "Failed to extract Node zip" }

    if (Test-Path $toolsNode) { Remove-Item $toolsNode -Recurse -Force }
    Move-Item $extracted.FullName $toolsNode
    Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
    Remove-Item $extractTo -Recurse -Force -ErrorAction SilentlyContinue

    if (-not (Test-Path $nodeExe)) { throw "Portable Node install failed" }
    Write-Host ("Node installed to {0}" -f $toolsNode) -ForegroundColor Green
    return @{ Node = $nodeExe; Npm = $npmCmd }
}

Write-Step "Prepare Node.js"
$sysNode = Find-SystemNode
$nodeExe = $null
$npmCmd = $null

if ($sysNode) {
    $ver = & $sysNode -v
    Write-Host ("System Node: {0} ({1})" -f $sysNode, $ver)
    $major = 0
    if ($ver -match 'v?(\d+)') { $major = [int]$Matches[1] }
    if ($major -ge 22) {
        $nodeExe = $sysNode
        $npm = Get-Command npm -ErrorAction SilentlyContinue
        if ($npm) { $npmCmd = $npm.Source }
    }
    else {
        Write-Host "System Node is too old (need >= 22.13). Using portable Node." -ForegroundColor Yellow
    }
}

if (-not $nodeExe -or -not $npmCmd) {
    $portable = Ensure-PortableNode
    $nodeExe = $portable.Node
    $npmCmd = $portable.Npm
}

Write-Step "npm install @cursor/sdk"
if (-not (Test-Path $bridgeDir)) {
    throw ("Bridge directory missing: {0}" -f $bridgeDir)
}
Push-Location $bridgeDir
try {
    & $npmCmd install --no-fund --no-audit
    if ($LASTEXITCODE -ne 0) { throw "npm install failed" }
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host ("Bridge ready: {0}" -f $bridgeDir) -ForegroundColor Green
Write-Host ("Node: {0}" -f $nodeExe)

# Write local config example (no secrets)
$example = Join-Path $repoRoot "src\StockSim.Web\appsettings.Cursor.local.json.example"
$exampleObj = [ordered]@{
    AI = [ordered]@{
        Cursor = [ordered]@{
            Enabled               = $true
            Model                 = "composer-2.5"
            TimeoutSeconds        = 180
            FallbackToPlaceholder = $true
            NodeExecutable        = $nodeExe
            BridgeDirectory       = "adapters/cursor-agent-bridge"
            ApiKey                = ""
        }
    }
}
$exampleJson = ($exampleObj | ConvertTo-Json -Depth 6)
[System.IO.File]::WriteAllText($example, $exampleJson + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

if ($Login) {
    Write-Step "Browser login to Cursor account"
    Write-Host "A browser window will open. Sign in with your current Cursor account." -ForegroundColor Yellow
    & $nodeExe (Join-Path $bridgeDir "login.mjs")
    if ($LASTEXITCODE -ne 0) { throw "Login failed" }
}

Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Prefer: .\scripts\setup-cursor-agent.ps1 -Login"
Write-Host "     or set user env CURSOR_API_KEY (https://cursor.com/dashboard/integrations)"
Write-Host "  2. Ensure Web appsettings AI:Cursor:Enabled = true"
Write-Host "  3. Restart StockSim.Web, then submit stock-pick prompts in AI Workbench"
Write-Host "Note: Cursor does not support username/password for Agent; use API Key or browser login." -ForegroundColor Cyan
