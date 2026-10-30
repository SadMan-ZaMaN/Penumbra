@echo off
REM Starts the overlay (which starts the Python engine itself). Look for the ring icon in the tray.
REM If it's already running, this opens its settings panel instead.
cd /d "%~dp0"
start "" "overlay\bin\Release\net8.0-windows\LumnWin.exe"
