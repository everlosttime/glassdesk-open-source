@echo off
net session >nul 2>&1
if errorlevel 1 (
    powershell.exe -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-FanSensor.ps1"
echo.
if errorlevel 1 (
    echo Installation failed. Send the error text to Codex.
    pause
    exit /b 1
)
echo GlassDesk Fan Sensor installed and started successfully.
pause
