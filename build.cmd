@echo off
rem Builds BlackHole.exe with the C# compiler that ships with Windows (no SDK needed), then starts it.
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo C# compiler not found at %CSC%
  echo This program needs 64-bit Windows 10/11 with .NET Framework 4.x.
  pause
  exit /b 1
)
taskkill /im BlackHole.exe /f >nul 2>&1
"%CSC%" /nologo /platform:x64 /optimize+ /target:winexe /out:BlackHole.exe /r:System.Drawing.dll /r:System.Windows.Forms.dll Native.cs ShellDesktop.cs Lens.cs Renderer.cs TaskbarFx.cs Program.cs
if errorlevel 1 (
  echo.
  echo BUILD FAILED - copy the error above and send it to the author.
  pause
  exit /b 1
)
echo.
echo OK: BlackHole.exe built. Starting it...
start "" "%~dp0BlackHole.exe"
echo A black-hole icon appears in the system tray. Close/minimize all windows to see the hole on the desktop.
echo If nothing appears, right-click the tray icon - Open log, or open %%APPDATA%%\BlackHole\log.txt
pause
