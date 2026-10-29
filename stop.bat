@echo off
REM Stops the overlay and the Python engine it started.
taskkill /im LumnWin.exe /t /f >nul 2>nul && echo LumnWin stopped. || echo LumnWin is not running.
timeout /t 2 >nul
