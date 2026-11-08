@echo off
title APFS Mount for Windows - Setup
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1"
if errorlevel 1 (
  echo Setup did not complete. Review the error above.
  pause
)
