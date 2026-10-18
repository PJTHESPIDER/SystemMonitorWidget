@echo off
setlocal
echo =======================================================
echo          System Monitor - Windows 11 Widget
echo =======================================================
echo.

echo Registering Widget Package with Windows...
powershell -NoProfile -Command "Add-AppxPackage -Register '%~dp0SystemMonitor\bin\x64\Debug\net8.0-windows10.0.22000.0\win-x64\AppxManifest.xml' -ErrorAction Continue"

echo.
echo =======================================================
echo [SUCCESS] Widget registered with Windows 11!
echo.
echo How to view your widget in the Widget Panel:
echo   1. Press [Win + W] (or click the Widgets icon on your taskbar)
echo   2. Click the '+' (Add widgets) button in the top right
echo   3. Find 'System Monitor' in the list and click 'Pin'
echo =======================================================
echo.
pause
endlocal
