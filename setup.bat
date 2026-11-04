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

echo Adding Penumbra to the Start menu and the desktop ...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$exe = '%~dp0overlay\bin\Release\net8.0-windows\Penumbra.exe'; $sh = New-Object -ComObject WScript.Shell;" ^
  "foreach ($d in [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop')) {" ^
  "$l = $sh.CreateShortcut((Join-Path $d 'Penumbra.lnk')); $l.TargetPath = $exe; $l.Arguments = '--panel';" ^
  "$l.WorkingDirectory = Split-Path $exe; $l.IconLocation = '%~dp0overlay\Penumbra.ico,0';" ^
  "$l.Description = 'Ambient light that moves with your music'; $l.Save() }"

echo.
echo Done. Start it from the Start menu or the desktop icon (or run.bat)
pause
exit /b 0

:err
echo.
echo Setup failed - see the message above.
pause
exit /b 1
