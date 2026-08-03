@echo off
setlocal
cd /d "%~dp0"

rem Release build into bin\. Cleans stale outputs first so NetBeauty / dependency
rem churn does not leave orphan DLLs. Strips PDBs for a shippable tree.
rem The main project also publishes the single-file MCP adapter beside the app.
taskkill /f /im "JeekRemoteManager.exe" >nul 2>nul

del /q "bin\*.deps.json" "bin\*.runtimeconfig.json" "bin\*.dll" "bin\*.pdb" "bin\Libs\*" 2>nul
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
