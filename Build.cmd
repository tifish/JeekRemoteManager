@echo off
setlocal
cd /d "%~dp0"

rem Release build into bin\. Cleans stale outputs first so NetBeauty / dependency
rem churn does not leave orphan DLLs. Strips PDBs for a shippable tree.
rem The main project also publishes the single-file MCP adapter beside the app.
set "APP_EXE=%~dp0bin\JeekRemoteManager.exe"
powershell.exe -NoProfile -Command "$target=[IO.Path]::GetFullPath($env:APP_EXE); foreach($process in (Get-CimInstance Win32_Process -Filter 'Name=''JeekRemoteManager.exe''')) { if($process.ExecutablePath -and [IO.Path]::GetFullPath($process.ExecutablePath) -eq $target) { Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue } }"

del /q "bin\*.deps.json" "bin\*.runtimeconfig.json" "bin\*.dll" "bin\*.pdb" 2>nul
rd /s /q "bin\Libs" 2>nul
rd /s /q "bin\Logs" 2>nul

dotnet build --configuration Release "%~dp0JeekRemoteManager\JeekRemoteManager.csproj"
if errorlevel 1 (
    echo.
    echo Build FAILED.
    pause
    exit /b 1
)

del /q /s bin\*.pdb 2>nul

echo.
echo Build succeeded -^> "%~dp0bin"
endlocal
