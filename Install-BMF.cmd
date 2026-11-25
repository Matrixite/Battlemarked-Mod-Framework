@echo off
setlocal
set "GAME=D:\SteamLibrary\steamapps\common\Battlemarked"
if not exist "%GAME%\battlemarked.exe" (
  echo Default game directory not found: %GAME%
  echo Edit this CMD file or run the PowerShell script with your actual GameDir.
  pause
  exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-Install.ps1" -GameDir "%GAME%" -Install -IncludeExample
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" echo Build or install failed. Copy the error output for troubleshooting.
if "%RESULT%"=="0" echo Build and install completed. Launch Battlemarked in solo mode.
pause
exit /b %RESULT%
