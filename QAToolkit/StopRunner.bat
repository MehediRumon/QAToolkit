@echo off
echo ========================================
echo   Stopping QAToolkit Runner
echo ========================================
echo.

:: Kill by window title
taskkill /F /FI "WINDOWTITLE eq QAToolkit Runner Service" 2>NUL

:: Also kill any dotnet process running QAToolkit
for /f "tokens=2" %%a in ('tasklist /v /fo list ^| findstr /i "QAToolkit"') do (
    taskkill /F /PID %%a 2>NUL
)

echo Runner stopped.
timeout /t 3
