@echo off
setlocal

fsutil dirty query %systemdrive% >nul 2>&1
if errorlevel 1 (
    echo Start-Process $env:ComSpec '/s /c "cd /d "%cd%" && "%~f0" %*"' -Verb RunAs -PassThru ^| Wait-Process> "%temp%\getadmin.ps1"
    powershell -ExecutionPolicy Bypass -File "%temp%\getadmin.ps1"
    set "ELEV_ERR=%errorlevel%"
    del /q "%temp%\getadmin.ps1" >nul 2>&1
    exit /b %ELEV_ERR%
)

echo Installing .NET...
powershell.exe -ExecutionPolicy Bypass -File "%~dp0dotnet-install.ps1" -Channel 10.0 -Runtime dotnet -Architecture x64 -InstallDir "%ProgramFiles%\dotnet"
if errorlevel 1 (
    echo .NET installation failed.
    exit /b 1
)

echo Starting application...
start "" "%~dp0JeekRemoteManager.exe"

endlocal
