@echo off
chcp 65001 >nul
cd /d "%~dp0.."
echo ========================================
echo   StockSim - Login Cursor Agent
echo ========================================
echo.
echo Browser will open. Sign in with your current Cursor account.
echo Credentials are stored locally under ~/.cursor/sdk/ only.
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-cursor-agent.ps1" -Login
if errorlevel 1 (
  echo.
  echo Login failed. You can also create an API Key at:
  echo   https://cursor.com/dashboard/integrations
  echo then set user env CURSOR_API_KEY and restart Web.
  pause
  exit /b 1
)

echo.
echo Login succeeded. Restart StockSim.Web before trying AI stock pick.
pause
