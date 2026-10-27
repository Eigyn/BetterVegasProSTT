@echo off
rem Installs or updates Subtitler for VEGAS Pro. Double-click to run; no administrator rights needed.
title Subtitler setup
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup\Install.ps1" %*
echo.
pause
