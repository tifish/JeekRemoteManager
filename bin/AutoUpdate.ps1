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

try {
    Get-Process -Name $appName -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            $_.WaitForExit()
        } catch {}
    }

    Remove-Item -Recurse -Force -LiteralPath $stageRoot -ErrorAction SilentlyContinue
    Remove-Item -Force -LiteralPath $packPath -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

    $client = New-Object System.Net.WebClient
    $client.Headers.Add("User-Agent", "$appName-Updater/1.0")
    $client.DownloadFile($downloadUrl, $packPath)

    if (-not (Test-Path -LiteralPath $packPath)) {
        Exit 1
    }

    Expand-Archive -Path $packPath -DestinationPath $stageDir -Force

    $stagedExe = Join-Path $stageDir "$appName.exe"
    if (-not (Test-Path -LiteralPath $stagedExe)) {
        Exit 1
    }

    # Preserve user data (portable mode) and the updater itself.
    $preserveNames = @("Connections", "AutoUpdate.ps1")
    Get-ChildItem -LiteralPath $installDir -Force -ErrorAction SilentlyContinue |
        Where-Object { $preserveNames -notcontains $_.Name } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    Copy-Item -Path (Join-Path $stageDir "*") -Destination $installDir -Recurse -Force

    Remove-Item -Recurse -Force -LiteralPath $stageRoot -ErrorAction SilentlyContinue
    Remove-Item -Force -LiteralPath $packPath -ErrorAction SilentlyContinue

    $exePath = Join-Path $installDir "$appName.exe"
    if (Test-Path -LiteralPath $exePath) {
        Start-Process -FilePath $exePath
    }
}
catch {
    Exit 1
}
