@echo off
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo ERROR: .NET Framework compiler was not found.
    exit /b 1
)
"%CSC%" /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ /win32icon:EyesOff.ico /win32manifest:EyesOff.manifest /out:EyesOff.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.dll EyesOff.cs
if errorlevel 1 exit /b 1
if not exist "protect.txt" copy /y "protect.example.txt" "protect.txt" >nul
echo Built EyesOff.exe. Run install_shortcut.bat to set up shortcuts.
exit /b 0
