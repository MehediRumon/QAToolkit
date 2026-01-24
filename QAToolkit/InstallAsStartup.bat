@echo off
echo ========================================
echo   Install QAToolkit Runner at Startup
echo ========================================
echo.

:: Get the current directory
set "RUNNER_PATH=%~dp0"

:: Create a shortcut in the Startup folder
set "STARTUP_FOLDER=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup"
set "SHORTCUT=%STARTUP_FOLDER%\QAToolkit Runner.lnk"

:: Use PowerShell to create the shortcut
powershell -Command "$ws = New-Object -ComObject WScript.Shell; $s = $ws.CreateShortcut('%SHORTCUT%'); $s.TargetPath = '%RUNNER_PATH%StartRunnerBackground.bat'; $s.WorkingDirectory = '%RUNNER_PATH%'; $s.WindowStyle = 7; $s.Save()"

if exist "%SHORTCUT%" (
    echo SUCCESS: Runner will start automatically on Windows startup.
    echo.
    echo Shortcut created at:
    echo %SHORTCUT%
) else (
    echo FAILED: Could not create startup shortcut.
)

echo.
pause
