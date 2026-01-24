@echo off
cd /d "%~dp0"

echo ========================================
echo   QAToolkit Remote Test Runner
echo ========================================
echo.

:: Kill any existing instance
taskkill /F /FI "WINDOWTITLE eq QAToolkit Runner Service" 2>NUL

:: Create logs folder if not exists
if not exist "logs" mkdir logs

:: Get current date/time for log file
for /f "tokens=2 delims==" %%I in ('wmic os get localdatetime /value') do set datetime=%%I
set logfile=logs\runner_%datetime:~0,8%.log

echo Starting QAToolkit Runner in background...
echo Log file: %logfile%
echo.

:: Start in background with minimized window
start "QAToolkit Runner Service" /MIN cmd /c "dotnet run --urls http://localhost:5000 >> %logfile% 2>&1"

echo Runner started successfully!
echo.
echo To stop the runner, run: StopRunner.bat
echo To view logs: type %logfile%
echo.

timeout /t 5
