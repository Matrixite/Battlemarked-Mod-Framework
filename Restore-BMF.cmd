@echo off
setlocal
echo Restore the latest BMF DLL snapshot? Existing configuration and saves will be kept.
choice /C YN /N /M "Continue [Y/N]? "
if errorlevel 2 exit /b 0
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-Install.ps1" -RestoreLatest %*
set "RESULT=%ERRORLEVEL%"
pause
exit /b %RESULT%
