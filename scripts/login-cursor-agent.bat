@echo off
chcp 65001 >nul
cd /d "%~dp0.."
echo ========================================
echo   StockSim - 登录 Cursor Agent（浏览器）
echo ========================================
echo.
echo 将打开浏览器，请用你当前的 Cursor 账号完成授权。
echo 不会保存账号密码；密钥仅写入本机 ~/.cursor/sdk/
echo.

if not exist "tools\node\node.exe" (
  echo [!] 未找到便携 Node，先执行安装...
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-cursor-agent.ps1"
  if errorlevel 1 (
    echo 安装失败
    pause
    exit /b 1
  )
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-cursor-agent.ps1" -Login -SkipDownloadNode
if errorlevel 1 (
  echo.
  echo 登录失败。也可到 https://cursor.com/dashboard/integrations 创建 API Key，
  echo 设置用户环境变量 CURSOR_API_KEY 后重启 Web。
  pause
  exit /b 1
)

echo.
echo 登录成功。请重启 StockSim.Web 后再试 AI 选股。
pause
