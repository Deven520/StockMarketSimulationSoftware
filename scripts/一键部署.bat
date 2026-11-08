@echo off
chcp 65001 >nul
cd /d "%~dp0.."
echo ========================================
echo   StockSim one-click deploy
echo ========================================
echo If DB exists, you will be asked Keep(K) or Reset(R).
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy-local.ps1" -Start
if errorlevel 1 (
  echo.
  echo Deploy failed. See errors above.
  pause
  exit /b 1
)

echo.
echo Deploy finished. Closing this window does not stop Web/Worker.
pause
