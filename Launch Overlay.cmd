@echo off
setlocal
cd /d "%~dp0"
dotnet build -c Release
if errorlevel 1 (
  echo.
  echo The overlay could not be built. Make sure an older copy is closed,
  echo then press any key to close this window.
  pause >nul
  exit /b 1
)
start "" "bin\Release\net8.0-windows\AnimalHospitalOverlay.exe"
