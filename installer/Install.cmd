@echo off
rem Installs Controller Toggle (the Game Bar widget + helper service). Asks for administrator rights.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" %*
