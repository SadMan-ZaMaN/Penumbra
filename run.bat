@echo off
REM Starts the overlay (which starts the Python engine itself). Look for the ring icon in the tray.
cd /d "%~dp0"
start "" "overlay\bin\Release\net8.0-windows\LumnWin.exe"
