@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -STA -File "%~dp0Tessera.Viewer.ps1" -Latest %*
if errorlevel 1 pause
