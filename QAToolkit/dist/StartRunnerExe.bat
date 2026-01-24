@echo off
title QAToolkit Runner Service
cd /d "%~dp0"

echo ========================================
echo   QAToolkit Remote Test Runner
echo ========================================
echo.

:: Create logs folder
if not exist "logs" mkdir logs

:: Get date for log
for /f "tokens=2 delims==" %%I in ('wmic os get localdatetime /value') do set datetime=%%I
set logfile=logs\runner_%datetime:~0,8%.log

echo Starting QAToolkit Runner...
echo Log: %logfile%
echo.

:: Run the executable
QAToolkit.exe --urls "http://localhost:5000" >> %logfile% 2>&1
