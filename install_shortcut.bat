@echo off
setlocal
cd /d "%~dp0"
if not exist "EyesOff.exe" (
    echo ERROR: EyesOff.exe was not found. Run build.bat first.
    pause
    exit /b 1
)
rem This command only installs shortcuts. It never switches off the display.
start "" /wait "%~dp0EyesOff.exe" --install-shortcuts
if not exist "shortcut_result.txt" (
    echo ERROR: shortcut_result.txt was not created.
    pause
    exit /b 1
)
chcp 65001 >nul
type "shortcut_result.txt"
echo.
echo NOTE: Existing EyesOff shortcuts may be replaced. Keep this folder in place.
pause
