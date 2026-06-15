@echo off
rem Builds Jeek Remote Manager. Usage: build.cmd [Debug|Release]
setlocal
set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Debug"

echo Building (%CONFIG%)...
dotnet build "%~dp0JeekRemoteManager\JeekRemoteManager.csproj" -c %CONFIG%
if errorlevel 1 (
    echo.
    echo Build FAILED.
    pause
    exit /b 1
)

echo.
echo Build succeeded -^> "%~dp0bin"
pause
