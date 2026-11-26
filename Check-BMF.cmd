@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-Install.ps1" -CheckOnly %*
set "RESULT=%ERRORLEVEL%"
pause
exit /b %RESULT%
