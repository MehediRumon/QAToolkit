@echo off
echo ========================================
echo   Remove QAToolkit Runner from Startup
echo ========================================
echo.

set "SHORTCUT=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\QAToolkit Runner.lnk"

if exist "%SHORTCUT%" (
    del "%SHORTCUT%"
    echo SUCCESS: Runner removed from Windows startup.
) else (
    echo Runner was not installed in startup.
)

echo.
pause
