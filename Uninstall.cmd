@echo off
rem Removes Subtitler from VEGAS Pro, and optionally its runtime and models.
title Subtitler uninstall
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup\Uninstall.ps1" %*
echo.
pause
