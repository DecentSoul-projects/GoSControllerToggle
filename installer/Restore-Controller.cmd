@echo off
setlocal
rem Emergency: turns the built-in controller back on and switches auto mode off,
rem even if the helper service is stopped, broken, or blocked by Windows.
rem (fltmc only works as administrator; unlike "net session" it doesn't need the Server service.)
fltmc >nul 2>&1
if errorlevel 1 (
    if /i "%~1"=="/elevated" (
        echo Administrator rights are needed to restore the controller.
        pause
        exit /b 1
    )
    echo Asking for administrator rights...
    powershell.exe -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '/elevated' -Verb RunAs"
    exit /b
)

"%~dp0ControllerToggleHelper.exe" --restore
if not errorlevel 1 goto done

rem The helper couldn't do it (Smart App Control blocks it, for example). Use Windows' own
rem pnputil instead: enable the device the helper last managed and the Legion Go S gamepad,
rem then reset the saved settings so auto mode is off if the helper runs again.
echo.
echo The helper couldn't restore the controller, so using Windows' pnputil instead...
set "SETTINGS=%ProgramData%\ControllerToggle\settings.ini"
if exist "%SETTINGS%" (
    for /f "usebackq tokens=1,* delims==" %%a in ("%SETTINGS%") do (
        if /i "%%a"=="TargetInstanceId" if not "%%b"=="" pnputil /enable-device "%%b"
    )
    del /f /q "%SETTINGS%" >nul 2>&1
)
pnputil /enable-device /deviceid "USB\VID_1A86&PID_E310&MI_01"
echo.
echo If the controller still doesn't respond, restart the PC, or enable it in Device Manager.

:done
echo.
pause
