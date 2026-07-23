@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Publish-WindowsApps.ps1"
if errorlevel 1 (
    echo.
    echo Publish failed.
    pause
    exit /b 1
)
echo.
echo Windows applications are ready in the dist folder.
pause
