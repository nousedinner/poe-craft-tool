@echo off
setlocal
set "PSModulePath="
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Run-UpgradeChecks.ps1"
set "checkExitCode=%ERRORLEVEL%"
pause
exit /b %checkExitCode%
