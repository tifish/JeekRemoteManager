$ErrorActionPreference = "Stop"
$appName = "JeekRemoteManager"

if ($args.Count -eq 0) {
    Exit 1
}

$downloadUrl = $args[0]
$installDir = $PSScriptRoot
$packPath = Join-Path $env:TEMP "$appName-update.zip"
$stageRoot = Join-Path $env:TEMP "$appName-update"
$stageDir = Join-Path $stageRoot "package"

$Host.UI.RawUI.WindowTitle = "$appName Updater"

Write-Host "================================================================"
Write-Host " $appName - Auto Update"
Write-Host "================================================================"
Write-Host ""
Write-Host "Please keep this window open. The app will restart automatically"
Write-Host "when the update is finished."
Write-Host ""

try {
    Write-Host "[1/5] Waiting for $appName to exit..."
    Get-Process -Name $appName -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            $_.WaitForExit()
        } catch {}
    }

    Write-Host "[2/5] Preparing temporary folders..."
    Remove-Item -Recurse -Force -LiteralPath $stageRoot -ErrorAction SilentlyContinue
    Remove-Item -Force -LiteralPath $packPath -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

    Write-Host "[3/5] Downloading update package..."
    Write-Host "      $downloadUrl"
    $client = New-Object System.Net.WebClient
    $client.Headers.Add("User-Agent", "$appName-Updater/1.0")
    $client.DownloadFile($downloadUrl, $packPath)

    if (-not (Test-Path -LiteralPath $packPath)) {
        Write-Host "Download failed." -ForegroundColor Red
        Start-Sleep -Seconds 5
        Exit 1
    }

    Write-Host "[4/5] Extracting and installing files..."
    Expand-Archive -Path $packPath -DestinationPath $stageDir -Force

    $stagedExe = Join-Path $stageDir "$appName.exe"
    if (-not (Test-Path -LiteralPath $stagedExe)) {
        Write-Host "Update package is missing $appName.exe." -ForegroundColor Red
        Start-Sleep -Seconds 5
        Exit 1
    }

    # Preserve portable user data, legacy top-level user data, and the updater itself.
    $preserveNames = @("Config", "Connections", "Scripts", "AutoUpdate.ps1")
    Get-ChildItem -LiteralPath $installDir -Force -ErrorAction SilentlyContinue |
        Where-Object { $preserveNames -notcontains $_.Name } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    Copy-Item -Path (Join-Path $stageDir "*") -Destination $installDir -Recurse -Force

    Remove-Item -Recurse -Force -LiteralPath $stageRoot -ErrorAction SilentlyContinue
    Remove-Item -Force -LiteralPath $packPath -ErrorAction SilentlyContinue

    Write-Host "[5/5] Restarting $appName..."
    $exePath = Join-Path $installDir "$appName.exe"
    if (Test-Path -LiteralPath $exePath) {
        Start-Process -FilePath $exePath
    }

    Write-Host ""
    Write-Host "Update completed." -ForegroundColor Green
}
catch {
    Write-Host ""
    Write-Host "Update failed: $($_.Exception.Message)" -ForegroundColor Red
    Start-Sleep -Seconds 5
    Exit 1
}
