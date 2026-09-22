@echo off
cd /d "%~dp0"
if not exist "artifacts\app\Translumo.exe" (
  echo Run this once in PowerShell: .\scripts\setup.ps1
  pause
  exit /b 1
)
start "" "artifacts\app\Translumo.exe"
