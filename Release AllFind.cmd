@echo off
rem Double-click to publish a new AllFind version to GitHub. Installed copies update themselves.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0src\release.ps1"
echo.
pause
