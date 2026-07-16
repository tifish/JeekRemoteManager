@echo off
rem Builds then launches Jeek Remote Manager. Usage: run.cmd [Debug|Release]
setlocal
set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Debug"

rem Stop only the copy built by this worktree. Other worktree Debug instances
rem intentionally keep running for side-by-side verification.
set "APP_EXE=%~dp0bin\JeekRemoteManager.exe"
powershell.exe -NoProfile -Command "$target=[IO.Path]::GetFullPath($env:APP_EXE); foreach($process in (Get-CimInstance Win32_Process -Filter 'Name=''JeekRemoteManager.exe''')) { if($process.ExecutablePath -and [IO.Path]::GetFullPath($process.ExecutablePath) -eq $target) { Stop-Process -Id $process.ProcessId -Force } }"

echo Building (%CONFIG%)...
dotnet build "%~dp0JeekRemoteManager\JeekRemoteManager.csproj" -c %CONFIG%
if errorlevel 1 (
    echo.
    echo Build FAILED.
    pause
    exit /b 1
)

rem Launch via Launch.cmd (WMI). Safe for Grok Build's kill-on-close job;
rem other agents can use plain start, but this path works everywhere.
call "%~dp0Launch.cmd"
exit /b %ERRORLEVEL%
