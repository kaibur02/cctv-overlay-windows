@echo off
rem cctv-overlay installer. Runs install.ps1 without changing your PowerShell execution policy.
rem   install.cmd [--check | --set-password | --test | --uninstall | --answers FILE --yes]
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
exit /b %ERRORLEVEL%
