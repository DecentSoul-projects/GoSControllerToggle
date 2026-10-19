@echo off
rem Uninstalls Controller Toggle and turns the built-in controller back on.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall.ps1" %*
