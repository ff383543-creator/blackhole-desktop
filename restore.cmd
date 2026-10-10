@echo off
rem Emergency reset: bring back the Recycle Bin icon and normal mouse cursors.
BlackHole.exe --quit
timeout /t 2 /nobreak >nul
BlackHole.exe --restore
