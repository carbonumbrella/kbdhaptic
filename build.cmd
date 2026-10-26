@echo off
rem Build kbdhaptic.exe with the in-box .NET Framework C# compiler (no Visual Studio).
setlocal
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set "CSC=%FW%\csc.exe"
if not exist "%CSC%" (
  echo ERROR: .NET Framework 4.x C# compiler not found at "%CSC%".
  exit /b 1
)
if not exist "release" mkdir "release"
"%CSC%" /nologo /target:winexe /platform:x64 /win32manifest:"src\kbdhaptic.manifest" ^
  /win32icon:"src\kbdhaptic.ico" ^
  /r:"%FW%\System.Windows.Forms.dll" /r:"%FW%\System.Drawing.dll" ^
  /out:"release\kbdhaptic.exe" "src\kbdhaptic.cs"
echo build exit %ERRORLEVEL%
