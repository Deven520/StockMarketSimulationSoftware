@echo off
setlocal
cd /d "%~dp0.."

echo ========================================
echo   StockSim - Cursor Agent Login
echo ========================================
echo.
echo Opens browser. Sign in with your Cursor account.
echo Credential is stored under %%USERPROFILE%%\.cursor\sdk\
echo Password is NOT saved into this repository.
echo.

if not exist "tools\node\node.exe" (
  echo [!] Portable Node missing. Running setup first...
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-cursor-agent.ps1"
  if errorlevel 1 (
    echo Setup failed.
    pause
    exit /b 1
  )
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-cursor-agent.ps1" -Login -SkipDownloadNode
if errorlevel 1 (
  echo.
  echo Login failed.
  echo Create an API Key at https://cursor.com/dashboard/integrations
  echo then set user env CURSOR_API_KEY and restart Web.
  pause
  exit /b 1
)

echo.
echo Login succeeded. Restart StockSim.Web and retry AI stock-pick.
pause
endlocal
