@echo off
title QAToolkit Runner
cd /d "%~dp0"

echo ========================================
echo   QAToolkit Remote Test Runner
echo ========================================
echo.

:: Check if already running
tasklist /FI "WINDOWTITLE eq QAToolkit Runner Service" 2>NUL | find /I "cmd.exe" >NUL
if %ERRORLEVEL% EQU 0 (
    echo Runner is already running!
    pause
    exit /b
)

:: Start the runner
echo Starting QAToolkit Runner...
echo Remote Server: http://oslqa.runasp.net
echo.

dotnet run --urls "http://localhost:5000"

pause
