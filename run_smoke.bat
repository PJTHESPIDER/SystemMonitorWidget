@echo off
setlocal
set SYSTEMMONITOR_DIAGNOSTICS=1
set PATH_TO_EXE=E:\Code\SystemMonitor\SystemMonitor\bin\Release\net8.0-windows10.0.22000.0\win-x64\SystemMonitor.exe
set EXPORT_PATH=C:\Temp\smoke_inventory.json

echo === Running SystemMonitor in export mode ===
"%PATH_TO_EXE%" --export "%EXPORT_PATH%"
set EXIT_CODE=%errorlevel%
echo === Exit code: %EXIT_CODE% ===
echo === Checking for export file ===
if exist "%EXPORT_PATH%" (
    echo Export file exists
    dir "%EXPORT_PATH%"
    echo === Contents ===
    type "%EXPORT_PATH%"
) else (
    echo Export file NOT found
)
echo === Checking diagnostic log ===
type "%LOCALAPPDATA%\SystemMonitor\systemmonitor.log" 2>nul | findstr /c:"log-start" /c:"inventory-request" /c:"inventory-export"
endlocal