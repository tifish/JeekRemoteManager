@echo off
rem Thin wrapper: see Launch.ps1 / AGENTS.md (WMI launch escapes Grok Build Job Objects).
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Launch.ps1"
exit /b %ERRORLEVEL%
