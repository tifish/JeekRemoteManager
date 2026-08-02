@echo off
rem Stdio entry for this worktree's Debug MCP surface.
rem
rem Agents launch this script (not bin\JeekRemoteManagerMcp.exe) so a running MCP session
rem locks only the fixed per-user adapter under %%LocalAppData%%. Builds can overwrite the
rem side-by-side copy in this folder; the app copies that file to the fixed path on startup
rem (not during the build).
rem
rem Requires: the app has been built and launched at least once so the fixed adapter exists
rem and this worktree's instance is registered. Forwards stdio to the named pipe of the app
rem next to this script.

set "ADAPTER=%LocalAppData%\JeekRemoteManager\Mcp\JeekRemoteManagerMcp.exe"
set "APP=%~dp0JeekRemoteManager.exe"

if not exist "%ADAPTER%" (
  echo The fixed JeekRemoteManager MCP adapter is not installed at: 1>&2
  echo   %ADAPTER% 1>&2
  echo Build and launch JeekRemoteManager once, then retry. 1>&2
  exit /b 1
)

if not exist "%APP%" (
  echo JeekRemoteManager.exe was not found next to this script: 1>&2
  echo   %APP% 1>&2
  echo Build the Debug configuration into bin\ first. 1>&2
  exit /b 1
)

"%ADAPTER%" --surface debug --app "%APP%" %*

