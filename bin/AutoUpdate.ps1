$ErrorActionPreference = "Stop"
$appName = "JeekRemoteManager"

# The app has already downloaded, extracted, and verified the update package
# into a staging folder before launching this script. All that remains here is
# the short critical window: wait for the app to exit, swap the files, restart.

if ($args.Count -eq 0) {
    Exit 1
}

$stageDir = [IO.Path]::GetFullPath($args[0])
$installDir = $PSScriptRoot
$exePath = Join-Path $installDir "$appName.exe"

$Host.UI.RawUI.WindowTitle = "$appName Updater"

Write-Host "================================================================"
Write-Host " $appName - Auto Update"
Write-Host "================================================================"
Write-Host ""
Write-Host "Please keep this window open. The app will restart automatically"
Write-Host "when the update is finished."
Write-Host ""

try {
    $stagePrefix = $stageDir.TrimEnd('\') + '\'
    $installPrefix = $installDir.TrimEnd('\') + '\'
    if ($stagePrefix.StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        $installPrefix.StartsWith($stagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The installation and staging directories must be separate."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $stageDir "$appName.exe"))) {
        throw "Staged update package is missing $appName.exe: $stageDir"
    }

    Write-Host "[1/3] Waiting for $appName to exit..."
    $targetExe = [IO.Path]::GetFullPath($exePath)
    Get-Process -Name $appName -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            $procPath = $_.Path
            if ($procPath -and [IO.Path]::GetFullPath($procPath) -eq $targetExe) {
                $_.WaitForExit()
            }
        } catch {}
    }

    Write-Host "[2/3] Installing files..."
    # Exclude both ends so a package cannot overwrite portable data. Full paths
    # keep identically named bundled directories such as Data\Scripts in scope.
    $excludedDirectories = foreach ($name in @("Config", "Connections", "Scripts", "Logs")) {
        Join-Path $stageDir $name
        Join-Path $installDir $name
    }
    $extraOptions = @()
    if (-not (Test-Path -LiteralPath (Join-Path $stageDir "AutoUpdate.ps1"))) {
        $extraOptions = @("/XF", (Join-Path $installDir "AutoUpdate.ps1"))
    }
    robocopy $stageDir $installDir /MIR /XD $excludedDirectories @extraOptions /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "Failed to install staged files (robocopy exit code $LASTEXITCODE)."
    }

    # Remove the update root the app created (typically
    # %LOCALAPPDATA%\JeekRemoteManager\Update containing package\ + version.txt).
    # Fall back to deleting only the package folder for legacy layouts.
    $updateRoot = Split-Path -Parent $stageDir
    $updateRootName = Split-Path -Leaf $updateRoot
    $updatePrefix = $updateRoot.TrimEnd('\') + '\'
    if (($updateRootName -eq "Update" -or $updateRootName -eq "$appName-update") -and
        (Split-Path -Leaf $stageDir) -eq "package" -and
        -not $installPrefix.StartsWith($updatePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -Recurse -Force -LiteralPath $updateRoot -ErrorAction SilentlyContinue
    } else {
        Remove-Item -Recurse -Force -LiteralPath $stageDir -ErrorAction SilentlyContinue
    }

    Write-Host "[3/3] Restarting $appName..."
    if (Test-Path -LiteralPath $exePath) {
        Start-Process -FilePath $exePath -WorkingDirectory $installDir
    }

    Write-Host ""
    Write-Host "Update completed." -ForegroundColor Green
}
catch {
    Write-Host ""
    Write-Host "Update failed: $($_.Exception.Message)" -ForegroundColor Red
    # Best effort: bring the app back even if the install failed.
    if (Test-Path -LiteralPath $exePath) {
        Start-Process -FilePath $exePath -WorkingDirectory $installDir
    }
    Start-Sleep -Seconds 5
    Exit 1
}
