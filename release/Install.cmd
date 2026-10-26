@echo off
rem kbdhaptic installer: removes any previous version, then installs to
rem %LOCALAPPDATA%\kbdhaptic, registers a logon autostart task (no admin needed),
rem creates a desktop shortcut, and launches it. Existing kbdhaptic.ini is preserved.
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

echo Creating desktop shortcut ...
powershell -NoProfile -Command "$ws=New-Object -ComObject WScript.Shell; $p=[Environment]::GetFolderPath('Desktop'); $lnk=$ws.CreateShortcut($p+'\kbdhaptic.lnk'); $lnk.TargetPath='%DST%\kbdhaptic.exe'; $lnk.WorkingDirectory='%DST%'; $lnk.IconLocation='%DST%\kbdhaptic.exe,0'; $lnk.Description='kbdhaptic'; $lnk.Save()"
if errorlevel 1 (
  echo WARNING: could not create the desktop shortcut.
) else (
  echo Desktop shortcut created.
)

echo Starting kbdhaptic ...
start "" "%DST%\kbdhaptic.exe"

echo.
echo ------------------------------------------------------------
echo  Optional: Windows screen auto-rotation
echo ------------------------------------------------------------
echo  kbdhaptic can rotate the screen from the orientation sensor.
echo  On this handheld Windows' own auto-rotation never engages
echo  (the built-in sensor exposes no accelerometer), so kbdhaptic
echo  does the rotating itself. To make Windows show the
echo  "Auto-rotate" toggle and treat the device as a tablet, three
echo  values are written to the registry under HKLM. That needs
echo  administrator rights (Windows will show a UAC prompt), and you
echo  must reboot or sign out afterwards. The toggle then works as
echo  the rotation on/off lock.
echo.
if not exist "%~dp0Enable-AutoRotate.reg" goto skipreg
set "AR="
set /p "AR=Enable Windows auto-rotate support now? [y/N]: "
if /i not "%AR%"=="Y" goto skipreg
set "REGFILE=%~dp0Enable-AutoRotate.reg"
powershell -NoProfile -Command "Start-Process reg.exe -ArgumentList @('import','%REGFILE%') -Verb RunAs -Wait"
echo Applied. Reboot or sign out to finish enabling auto-rotate.
goto afterreg
:skipreg
echo Skipped. You can run Enable-AutoRotate.reg later to enable it.
:afterreg

echo.
echo Done. kbdhaptic is running in the tray and will start at logon.
echo (Your kbdhaptic.ini settings were kept.)
echo Open its settings from the tray icon (double-click).
pause
