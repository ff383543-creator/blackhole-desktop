@echo off
rem Builds BlackHole.exe with the C# compiler that ships with Windows (no SDK needed), then starts it.
cd /d "%~dp0"
echo Folder: %CD%
if not exist Program.cs (
  echo.
  echo Program.cs is missing next to build.cmd.
  echo If you downloaded a ZIP: right-click it, Extract All, and run build.cmd from the extracted folder.
  pause
  exit /b 1
)
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo.
  echo C# compiler not found at %CSC%
  echo This program needs 64-bit Windows 10/11 with .NET Framework 4.x.
  pause
  exit /b 1
)
taskkill /im BlackHole.exe /f >nul 2>&1
timeout /t 1 /nobreak >nul
if exist BlackHole.exe del BlackHole.exe
"%CSC%" /nologo /platform:x64 /optimize+ /target:winexe /out:BlackHole.exe /r:System.Drawing.dll /r:System.Windows.Forms.dll Native.cs ShellDesktop.cs Lens.cs Renderer.cs TaskbarFx.cs Program.cs > build_log.txt 2>&1
type build_log.txt
if not exist BlackHole.exe (
  echo.
  echo BUILD FAILED - send build_log.txt to the author.
  pause
  exit /b 1
)
echo.
echo OK: BlackHole.exe built. Starting it...
start "" "%~dp0BlackHole.exe"
echo A black-hole icon appears in the system tray (arrow near the clock).
echo Close or minimize all windows (Win+D) to see the hole on the desktop.
echo Keys on the desktop: O = swallow everything, P = stop and put everything back.
echo If nothing appears: right-click the tray icon - Open log, or open %%APPDATA%%\BlackHole\log.txt
echo (If Windows SmartScreen or an antivirus blocks BlackHole.exe, allow it: it is built from the source here.)
pause
