@echo off
echo ========================================
echo   QAToolkit Runner Status
echo ========================================
echo.

:: Check if running
tasklist /FI "WINDOWTITLE eq QAToolkit Runner Service" 2>NUL | find /I "cmd.exe" >NUL
if %ERRORLEVEL% EQU 0 (
    echo Status: RUNNING
) else (
    echo Status: STOPPED
)

echo.
echo Checking local API...
curl -s http://localhost:5000/api/automation/health 2>NUL
if %ERRORLEVEL% NEQ 0 (
    echo Local API: Not responding
) else (
    echo.
)

echo.
echo Checking remote server connection...
curl -s http://oslqa.runasp.net/api/automation/health 2>NUL
if %ERRORLEVEL% NEQ 0 (
    echo Remote Server: Not reachable
)

echo.
pause
