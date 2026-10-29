@echo off
REM Runs the engine in this console with live readouts, and the overlay without its own engine.
REM Add --demo after engine.py to test the glow without any music playing.
cd /d "%~dp0"
start "" "overlay\bin\Release\net8.0-windows\LumnWin.exe" --no-engine
engine\.venv\Scripts\python engine\engine.py --debug %*
