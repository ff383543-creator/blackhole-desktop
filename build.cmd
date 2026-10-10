@echo off
rem Builds BlackHole.exe with the C# compiler that ships with Windows (no SDK needed).
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" /nologo /platform:x64 /optimize+ /target:winexe /out:BlackHole.exe /r:System.Drawing.dll /r:System.Windows.Forms.dll Native.cs ShellDesktop.cs Lens.cs Renderer.cs Program.cs
if errorlevel 1 (echo BUILD FAILED) else (echo OK: BlackHole.exe)
