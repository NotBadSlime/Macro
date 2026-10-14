@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-Installer.ps1" %*
if errorlevel 1 (
    echo Packaging failed. See the error above.
    pause
    exit /b 1
)
echo Packaging completed.
pause
