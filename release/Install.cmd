@echo off
rem kbdhaptic installer: removes any previous version, then installs to
rem %LOCALAPPDATA%\kbdhaptic, registers a logon autostart task (no admin needed),
rem and launches it. Existing kbdhaptic.ini settings are preserved.
setlocal
set "DST=%LOCALAPPDATA%\kbdhaptic"

echo Removing previous version ...
schtasks /End /TN "kbdhaptic" >nul 2>&1
taskkill /IM kbdhaptic.exe /F >nul 2>&1
timeout /t 1 /nobreak >nul
schtasks /Delete /TN "kbdhaptic" /F >nul 2>&1
if exist "%DST%\kbdhaptic.exe" del /q "%DST%\kbdhaptic.exe" >nul 2>&1
if exist "%DST%\kbdhaptic.log" del /q "%DST%\kbdhaptic.log" >nul 2>&1

echo Installing to "%DST%" ...
if not exist "%DST%" mkdir "%DST%"
copy /y "%~dp0kbdhaptic.exe" "%DST%\kbdhaptic.exe" >nul
if errorlevel 1 (
  echo ERROR: copy failed.
  pause
  exit /b 1
)

echo Registering logon autostart ...
schtasks /Create /TN "kbdhaptic" /TR "\"%DST%\kbdhaptic.exe\"" /SC ONLOGON /RL LIMITED /F >nul
if errorlevel 1 (
  echo WARNING: could not create the scheduled task. Autostart not set.
) else (
  echo Autostart task "kbdhaptic" created.
)

echo Starting kbdhaptic ...
start "" "%DST%\kbdhaptic.exe"

echo.
echo Done. kbdhaptic is running in the tray and will start at logon.
echo (Your kbdhaptic.ini settings were kept.)
echo Open its settings from the tray icon (double-click).
pause
