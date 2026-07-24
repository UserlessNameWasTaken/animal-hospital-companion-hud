@echo off
cd /d "%~dp0"
dotnet run --project AnimalHospitalTeam.SmokeTests -- https://relay.ahospitalhud.com
if errorlevel 1 (
  echo.
  echo Tunnel test failed. Keep this window open and share the error shown above.
) else (
  echo.
  echo Tunnel test passed.
)
pause
