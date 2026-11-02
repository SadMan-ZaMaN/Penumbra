@echo off
REM Stops the overlay and the Python engine it started.
taskkill /im Penumbra.exe /t /f >nul 2>nul && echo Penumbra stopped. || echo Penumbra is not running.
REM (the app was called LumnWin before; stop a copy of that too if one is still around)
taskkill /im LumnWin.exe /t /f >nul 2>nul
timeout /t 2 >nul
