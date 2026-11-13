@echo off
rem Try the script mirrors first, then GitHub directly. Execute only once.
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference = 'Stop'; [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12;" ^
  "$source = 'https://raw.githubusercontent.com/tifish/JeekRemoteManager/main/install.ps1';" ^
  "$urls = @('https://ghfast.top/' + $source; 'https://gh-proxy.com/' + $source; $source); $installer = $null;" ^
  "foreach ($url in $urls) { Write-Host ('Downloading installer from ' + $url); try {" ^
  "  $content = Invoke-RestMethod -Uri $url -TimeoutSec 30 -ErrorAction Stop;" ^
  "  if ($content -isnot [string] -or [string]::IsNullOrWhiteSpace($content) -or $content -match '^\s*<') { throw 'The server did not return a PowerShell installer.' };" ^
  "  $installer = [scriptblock]::Create($content); break;" ^
  "} catch { Write-Host ('  Failed: ' + $_.Exception.Message) -ForegroundColor Yellow } };" ^
  "if ($null -eq $installer) { Write-Host 'Installer download failed from all mirrors and GitHub.' -ForegroundColor Red; exit 1 };" ^
  "try { & $installer } catch { Write-Host ('Installation failed: ' + $_.Exception.Message) -ForegroundColor Red; exit 1 }"
exit /b %errorlevel%
