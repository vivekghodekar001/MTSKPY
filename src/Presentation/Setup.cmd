@echo off
setlocal enableextensions

rem ---------------------------------------------------------------------
rem  Renewable Energy (MTSKPY linestaff inspection) - folder setup.
rem
rem  Runs without administrator rights. It creates a desktop shortcut that
rem  points at THIS copy of the app - the folder it is run from - and then
rem  starts the app. Move the folder to another machine or another drive
rem  and run this file again: the shortcut follows the folder.
rem ---------------------------------------------------------------------

set "HERE=%~dp0"
if "%HERE:~-1%"=="\" set "HERE=%HERE:~0,-1%"
set "EXE=%HERE%\MSKPY-Windows.exe"

if not exist "%EXE%" (
  echo.
  echo   MSKPY-Windows.exe is not in this folder.
  echo   Put Setup.cmd back in the folder with the app and run it again.
  echo.
  pause
  exit /b 1
)

echo.
echo   Setting up Renewable Energy (MTSKPY Inspection) from:
echo     %HERE%
echo.

powershell -NoProfile -ExecutionPolicy Bypass -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'Renewable Energy (MTSKPY Inspection).lnk')); $s.TargetPath='%EXE%'; $s.WorkingDirectory='%HERE%'; $s.IconLocation='%EXE%,0'; $s.Description='MTSKPY linestaff inspection'; $s.Save()"
if errorlevel 1 (
  echo   The desktop shortcut could not be created.
  echo   You can still start the app from this folder:
  echo     %EXE%
  echo.
  pause
  exit /b 1
)

echo   Desktop shortcut created.
echo   Starting the app...
echo.
start "" "%EXE%"
endlocal
