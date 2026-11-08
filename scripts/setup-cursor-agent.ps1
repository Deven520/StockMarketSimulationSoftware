<#
.SYNOPSIS
  安装 StockSim ↔ Cursor Agent SDK 桥接（便携 Node + npm 依赖），可选浏览器登录当前 Cursor 账号。

.DESCRIPTION
  Cursor SDK 不使用账号密码，认证方式：
  1) -Login：浏览器登录当前 Cursor 账号，写入本机 SDK 凭证
  2) 环境变量 CURSOR_API_KEY（Dashboard → Integrations）
  3) appsettings 中 AI:Cursor:ApiKey（勿提交 Git）

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

function Write-Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }

function Find-SystemNode {
    $cmd = Get-Command node -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Ensure-PortableNode {
    $nodeExe = Join-Path $toolsNode "node.exe"
    $npmCmd = Join-Path $toolsNode "npm.cmd"
    if ((Test-Path $nodeExe) -and (Test-Path $npmCmd)) {
        Write-Host "已有便携 Node: $nodeExe" -ForegroundColor Green
        return @{ Node = $nodeExe; Npm = $npmCmd }
    }

    if ($SkipDownloadNode) {
        throw "tools\node 不存在且指定了 -SkipDownloadNode"
    }

    Write-Step "下载便携 Node.js $nodeVersion"
    New-Item -ItemType Directory -Force -Path (Join-Path $repoRoot "tools") | Out-Null
    $zipPath = Join-Path $repoRoot "tools\$zipName"
    Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing

    $extractTo = Join-Path $repoRoot "tools\_node_extract"
    if (Test-Path $extractTo) { Remove-Item $extractTo -Recurse -Force }
    Expand-Archive -Path $zipPath -DestinationPath $extractTo -Force

    $extracted = Get-ChildItem $extractTo -Directory | Select-Object -First 1
    if (-not $extracted) { throw "解压 Node 失败" }

    if (Test-Path $toolsNode) { Remove-Item $toolsNode -Recurse -Force }
    Move-Item $extracted.FullName $toolsNode
    Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
    Remove-Item $extractTo -Recurse -Force -ErrorAction SilentlyContinue

    if (-not (Test-Path $nodeExe)) { throw "便携 Node 安装失败" }
    Write-Host "Node 已安装到 $toolsNode" -ForegroundColor Green
    return @{ Node = $nodeExe; Npm = $npmCmd }
}

Write-Step "准备 Node.js"
$sysNode = Find-SystemNode
$nodeExe = $null
$npmCmd = $null

if ($sysNode) {
    $ver = & $sysNode -v
    Write-Host "系统 Node: $sysNode ($ver)"
    $major = 0
    if ($ver -match 'v?(\d+)') { $major = [int]$Matches[1] }
    if ($major -ge 22) {
        $nodeExe = $sysNode
        $npm = Get-Command npm -ErrorAction SilentlyContinue
        if ($npm) { $npmCmd = $npm.Source }
    }
    else {
        Write-Host "系统 Node 版本过低（需要 >= 22.13），改用便携 Node。" -ForegroundColor Yellow
    }
}

if (-not $nodeExe -or -not $npmCmd) {
    $portable = Ensure-PortableNode
    $nodeExe = $portable.Node
    $npmCmd = $portable.Npm
}

Write-Step "npm install @cursor/sdk"
Push-Location $bridgeDir
try {
    & $npmCmd install --no-fund --no-audit
    if ($LASTEXITCODE -ne 0) { throw "npm install 失败" }
}
finally {
    Pop-Location
}

Write-Host "`n桥接就绪: $bridgeDir" -ForegroundColor Green
Write-Host "Node: $nodeExe"

# 写入本地覆盖配置提示（不写密钥）
$example = Join-Path $repoRoot "src\StockSim.Web\appsettings.Cursor.local.json.example"
@"
{
  "AI": {
    "Cursor": {
      "Enabled": true,
      "Model": "composer-2.5",
      "TimeoutSeconds": 180,
      "FallbackToPlaceholder": true,
      "NodeExecutable": "$($nodeExe.Replace('\','\\'))",
      "BridgeDirectory": "adapters/cursor-agent-bridge",
      "ApiKey": ""
    }
  }
}
"@ | Set-Content -Path $example -Encoding UTF8

if ($Login) {
    Write-Step "浏览器登录 Cursor 账号（不会在本仓库保存密码）"
    Write-Host "将打开浏览器；请用你当前的 Cursor 账号完成授权。" -ForegroundColor Yellow
    & $nodeExe (Join-Path $bridgeDir "login.mjs")
    if ($LASTEXITCODE -ne 0) { throw "登录失败" }
}

Write-Host @"

下一步：
  1. （推荐）再跑一次带登录：  .\scripts\setup-cursor-agent.ps1 -Login
     或设置用户环境变量 CURSOR_API_KEY（https://cursor.com/dashboard/integrations）
  2. 确认 Web appsettings 中 AI:Cursor:Enabled = true
  3. 重启 StockSim.Web，在「AI 工作台」提交选股提示词

注意：Cursor 不提供用账号密码调 Agent 的方式；请用 API Key 或浏览器登录。
"@ -ForegroundColor Cyan
