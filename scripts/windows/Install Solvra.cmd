@echo off
rem Double-click to install Solvra (no administrator rights needed).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
echo.
pause
