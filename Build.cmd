@echo off
rem Builds Controller Toggle. Needs Visual Studio 2022 with "WinUI application development" + "Universal Windows Platform tools".
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\Build.ps1" %*
echo.
pause
