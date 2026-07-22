# JeekRemoteManager one-click installer.
# Usage:
#   irm https://raw.githubusercontent.com/tifish/JeekRemoteManager/main/install.ps1 | iex
# Mirror for mainland China:
#   irm https://ghfast.top/https://raw.githubusercontent.com/tifish/JeekRemoteManager/main/install.ps1 | iex
#
# No registry writes. To uninstall: quit the app, delete
# %LOCALAPPDATA%\Programs\JeekRemoteManager and the Start Menu shortcut.

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$AppName = "JeekRemoteManager"
$ZipName = "$AppName.zip"
$InstallDir = Join-Path $env:LOCALAPPDATA "Programs\$AppName"
$ShortcutPath = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$AppName.lnk"

# Fixed download address of the latest release; no GitHub API calls
# (api.github.com is rate-limited and unreachable from mainland China).
$DownloadUrl = "https://github.com/tifish/$AppName/releases/latest/download/$ZipName"

# Same mirror list as GitHubMirrors in JeekTools.NET.
$Mirrors = @(
    $DownloadUrl,
    ($DownloadUrl -replace '^https://github\.com/', 'https://ghfast.top/https://github.com/'),
    ($DownloadUrl -replace '^https://github\.com/', 'https://gh-proxy.com/github.com/')
)

# Abandon a mirror whose average speed over the last 10 seconds stays below
# 0.5 MB/s (after a short grace period), unless it is the last mirror.
$MinimumBytesPerSecond = 512KB
$SpeedWindowSeconds = 10
$GraceSeconds = 5

function Download-File {
    param(
        [string]$Url,
        [string]$Destination,
        [bool]$EnforceMinimumSpeed
    )

    $request = [System.Net.HttpWebRequest]::Create($Url)
    $request.Timeout = 30000
    $request.ReadWriteTimeout = 30000
    $request.AllowAutoRedirect = $true

    $response = $null
    $stream = $null
    $file = $null
    try {
        $response = $request.GetResponse()
        $stream = $response.GetResponseStream()
        $file = [System.IO.File]::Create($Destination)

        $buffer = New-Object byte[] 65536
        $total = [long]0
        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        # Sliding window of (seconds, totalBytes) samples for the speed check.
        $samples = New-Object System.Collections.Generic.Queue[object]
        $lastReport = 0.0

        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $file.Write($buffer, 0, $read)
            $total += $read
            $now = $stopwatch.Elapsed.TotalSeconds

            $samples.Enqueue(@($now, $total))
            while ($samples.Count -gt 0 -and $samples.Peek()[0] -lt $now - $SpeedWindowSeconds) {
                $null = $samples.Dequeue()
            }

            if ($now - $lastReport -ge 1) {
                $lastReport = $now
                $mb = $total / 1MB
                Write-Host ("`r  Downloaded {0:0.0} MB..." -f $mb) -NoNewline

                if ($EnforceMinimumSpeed -and $now -gt $GraceSeconds -and $samples.Count -gt 1) {
                    $oldest = $samples.Peek()
                    $windowSeconds = $now - $oldest[0]
                    if ($windowSeconds -ge $SpeedWindowSeconds - 1) {
                        $speed = ($total - $oldest[1]) / $windowSeconds
                        if ($speed -lt $MinimumBytesPerSecond) {
                            throw ("download speed {0:0.00} MB/s stayed below 0.5 MB/s" -f ($speed / 1MB))
                        }
                    }
                }
            }
        }
        Write-Host ""
    }
    finally {
        if ($file) { $file.Dispose() }
        if ($stream) { $stream.Dispose() }
        if ($response) { $response.Dispose() }
    }
}

Write-Host "=== $AppName Installer ===" -ForegroundColor Cyan

# 1. Download the latest release zip, falling back through mirrors.
$tempRoot = Join-Path $env:TEMP "$AppName-install"
if (Test-Path $tempRoot) {
    Remove-Item -Recurse -Force $tempRoot
}
New-Item -ItemType Directory -Path $tempRoot | Out-Null
$zipPath = Join-Path $tempRoot $ZipName

$downloaded = $false
for ($i = 0; $i -lt $Mirrors.Count; $i++) {
    $isLast = $i -eq $Mirrors.Count - 1
    Write-Host "Downloading from $($Mirrors[$i])"
    try {
        Download-File -Url $Mirrors[$i] -Destination $zipPath -EnforceMinimumSpeed (-not $isLast)
        $downloaded = $true
        break
    }
    catch {
        Write-Host "  Failed: $($_.Exception.Message)" -ForegroundColor Yellow
        if (Test-Path $zipPath) {
            Remove-Item -Force $zipPath
        }
    }
}
if (-not $downloaded) {
    Write-Host "Download failed from all mirrors." -ForegroundColor Red
    exit 1
}

# 2. Extract to a staging folder.
Write-Host "Extracting..."
$stageDir = Join-Path $tempRoot "package"
Expand-Archive -Path $zipPath -DestinationPath $stageDir -Force
if (-not (Test-Path (Join-Path $stageDir "$AppName.exe"))) {
    Write-Host "Package is missing $AppName.exe." -ForegroundColor Red
    exit 1
}

# 3. Stop only instances running from the install directory, so instances
#    running from a development folder are left alone.
Get-Process -Name $AppName -ErrorAction SilentlyContinue | ForEach-Object {
    $exePath = $null
    try { $exePath = $_.Path } catch {}
    if ($exePath -and $exePath.StartsWith("$InstallDir\", [System.StringComparison]::OrdinalIgnoreCase)) {
        Write-Host "Stopping running instance (PID $($_.Id))..."
        Stop-Process -Id $_.Id -Force
        $_.WaitForExit()
    }
}

# 4. Mirror the package into the install directory, cleaning up files the new
#    version no longer ships while preserving user data folders. The exclusion
#    list matches the auto-update script (bin/AutoUpdate.ps1).
Write-Host "Installing to $InstallDir"
robocopy $stageDir $InstallDir /MIR /XD Config Connections Scripts Logs /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) {
    Write-Host "Failed to copy files (robocopy exit code $LASTEXITCODE)." -ForegroundColor Red
    exit 1
}

Remove-Item -Recurse -Force $tempRoot

# 5. Create the Start Menu shortcut.
Write-Host "Creating Start Menu shortcut..."
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($ShortcutPath)
$shortcut.TargetPath = Join-Path $InstallDir "$AppName.exe"
$shortcut.WorkingDirectory = $InstallDir
$shortcut.Save()

# 6. Start the app; if the .NET runtime is missing, Setup.cmd installs it
#    (elevated) and then starts the app itself.
$hasRuntime = $false
try {
    $runtimes = & dotnet --list-runtimes 2>$null
    if ($runtimes -match 'Microsoft\.NETCore\.App 10\.') {
        $hasRuntime = $true
    }
}
catch {}

if ($hasRuntime) {
    Write-Host "Starting $AppName..."
    Start-Process -FilePath (Join-Path $InstallDir "$AppName.exe") -WorkingDirectory $InstallDir
}
else {
    Write-Host ".NET 10 runtime not found; running Setup.cmd to install it..."
    Start-Process -FilePath (Join-Path $InstallDir "Setup.cmd") -WorkingDirectory $InstallDir
}

Write-Host "Done." -ForegroundColor Green
