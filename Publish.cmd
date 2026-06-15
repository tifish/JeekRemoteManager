@echo off
rem Publishes an optimized build (ReadyToRun + NetBeauty) to .\publish.
rem Usage: publish.cmd [runtime] [self]
rem   runtime : RID, default win-x64
rem   self    : pass "self" for a self-contained build (no .NET runtime needed on target)
setlocal
set "RID=%~1"
if "%RID%"=="" set "RID=win-x64"

set "SC=--no-self-contained"
if /i "%~2"=="self" set "SC=--self-contained"

set "OUT=%~dp0publish"

echo Publishing Release for %RID% (ReadyToRun + NetBeauty, %SC%) -^> "%OUT%"
if exist "%OUT%" rmdir /s /q "%OUT%"

dotnet publish "%~dp0JeekRemoteManager\JeekRemoteManager.csproj" -c Release -r %RID% %SC% -p:PublishReadyToRun=true -p:PublishTrimmed=false -p:PublishSingleFile=false -o "%OUT%"
if errorlevel 1 (
    echo.
    echo Publish FAILED. If a file is locked, close the published app and retry.
    pause
    exit /b 1
)

echo.
echo Published -^> "%OUT%"
echo   Executable : %OUT%\JeekRemoteManager.exe
echo   Libraries  : %OUT%\libs  (moved there by NetBeauty)
pause
