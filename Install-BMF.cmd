@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-Install.ps1" -Install -IncludeExample %*
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" echo Build or install failed. Copy the error output for troubleshooting.
if "%RESULT%"=="0" echo Build and install completed. Launch Battlemarked in disposable solo mode.
pause
exit /b %RESULT%
