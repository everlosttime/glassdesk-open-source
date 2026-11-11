@echo off
setlocal
chcp 65001 >nul
set "reportRoot=%~dp0Reports\desktop-%RANDOM%-%RANDOM%"
mkdir "%reportRoot%"
echo Keep this test open for about two minutes. You can move windows behind it.
"%~dp0GlassDesk.LauncherAcceptance.exe" --desktop-backdrop --sample-seconds 60 --isolated-worker "%~dp0GlassCaptureProbe.exe" --data-dir "%reportRoot%\Data" --report "%reportRoot%\cases.json" >"%reportRoot%\run.log" 2>&1
set "result=%errorlevel%"
echo Diagnostic finished. Exit code: %result%
echo Report folder: %reportRoot%
if /i not "%~1"=="--no-pause" pause
exit /b %result%
