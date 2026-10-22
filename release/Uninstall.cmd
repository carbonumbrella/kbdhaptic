@echo off
rem kbdhaptic uninstaller: stops the app, removes the autostart task and the files.
setlocal
set "DST=%LOCALAPPDATA%\kbdhaptic"

echo Removing autostart task ...
schtasks /Delete /TN "kbdhaptic" /F >nul 2>&1

echo Stopping kbdhaptic ...
taskkill /IM kbdhaptic.exe /F >nul 2>&1
timeout /t 1 /nobreak >nul

echo Deleting files ...
rd /s /q "%DST%" 2>nul

echo.
echo kbdhaptic removed.
pause
