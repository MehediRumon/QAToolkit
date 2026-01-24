@echo off
echo ========================================
echo   Publishing QAToolkit for Distribution
echo ========================================
echo.

cd /d "%~dp0"

:: Clean previous publish
if exist "dist" rmdir /s /q "dist"

echo Building self-contained executable...
dotnet publish -c Release -r win-x64 --self-contained true -o dist

if %ERRORLEVEL% EQU 0 (
    echo.
    echo SUCCESS! Distribution files created in: dist\
    echo.
    echo Copy the "dist" folder to other PCs.
    echo Run "QAToolkit.exe" directly - no .NET SDK needed!

    :: Copy batch files to dist
    copy StartRunnerExe.bat dist\ >NUL 2>&1
) else (
    echo FAILED to publish.
)

pause
