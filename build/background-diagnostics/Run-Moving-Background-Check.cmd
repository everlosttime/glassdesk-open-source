@echo off
setlocal
chcp 65001 >nul
set "reportRoot=%~dp0Reports\moving-%RANDOM%-%RANDOM%"
mkdir "%reportRoot%"
echo This check shows a moving color background for about twenty seconds.
"%~dp0GlassDesk.LauncherAcceptance.exe" --continuous-backdrop --isolated-worker "%~dp0GlassCaptureProbe.exe" --moving-fixture "%~dp0GlassCaptureProbe.exe" --data-dir "%reportRoot%\Data" --report "%reportRoot%\cases.json" >"%reportRoot%\run.log" 2>&1
set "result=%errorlevel%"
echo Check finished. Exit code: %result%
echo Report folder: %reportRoot%
if /i not "%~1"=="--no-pause" pause
exit /b %result%
