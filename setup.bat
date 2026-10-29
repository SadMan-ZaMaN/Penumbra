@echo off
REM One-time setup: Python virtual env for the engine + first build of the overlay.
cd /d "%~dp0"

where py >nul 2>nul && (set PY=py -3) || (set PY=python)
echo Creating engine\.venv ...
%PY% -m venv engine\.venv || goto :err
engine\.venv\Scripts\python -m pip install --upgrade pip >nul
engine\.venv\Scripts\python -m pip install -r engine\requirements.txt || goto :err

echo Building overlay ...
dotnet build overlay -c Release || goto :err

echo.
echo Done. Start it with run.bat
pause
exit /b 0

:err
echo.
echo Setup failed - see the message above.
pause
exit /b 1
