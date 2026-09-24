# Run the installer's actual copy step against isolated directories, without
# downloading a release, stopping an app, or creating a shortcut.
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$installer = Get-Content -LiteralPath (Join-Path $repoRoot "install.ps1") -Raw
$copyStart = $installer.IndexOf('# 4. Mirror the package')
$copyEnd = $installer.IndexOf('Remove-Item -Recurse -Force $tempRoot', $copyStart)
if ($copyStart -lt 0 -or $copyEnd -lt 0) { throw "Installer copy step not found." }
$copyStep = [scriptblock]::Create($installer.Substring($copyStart, $copyEnd - $copyStart))
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("Jrm install test " + [guid]::NewGuid())
$stageDir = Join-Path $testRoot "package"
$InstallDir = Join-Path $testRoot "installed app"

function Write-FixtureFile([string]$root, [string]$relativePath, [string]$value) {
    $path = Join-Path $root $relativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    [IO.File]::WriteAllText($path, $value)
}

function Assert-File([string]$relativePath, [string]$expected) {
    $path = Join-Path $InstallDir $relativePath
    if (-not (Test-Path -LiteralPath $path) -or [IO.File]::ReadAllText($path) -cne $expected) {
        throw "Unexpected installed contents: $relativePath"
    }
}

try {
    Write-FixtureFile $stageDir "JeekRemoteManager.exe" "fixture"
    Write-FixtureFile $stageDir "Data\Scripts\Example suite\params.conf" "TARGET=string"
    Write-FixtureFile $stageDir "Data\Scripts\Example suite\run.sh" "echo first"
    foreach ($name in @("Config", "Connections", "Scripts", "Logs")) {
        Write-FixtureFile $stageDir "$name\package-only.txt" "must not install"
        Write-FixtureFile $stageDir "Data\$name\bundled.txt" "bundled"
    }

    & $copyStep
    Assert-File "Data\Scripts\Example suite\run.sh" "echo first"
    Assert-File "Data\Scripts\Example suite\params.conf" "TARGET=string"
    foreach ($name in @("Config", "Connections", "Scripts", "Logs")) {
        Assert-File "Data\$name\bundled.txt" "bundled"
        if (Test-Path -LiteralPath (Join-Path $InstallDir $name)) { throw "Copied root user folder: $name" }
        Write-FixtureFile $InstallDir "$name\user-only.txt" "keep user data"
        Write-FixtureFile $InstallDir "$name\package-only.txt" "keep local version"
    }

    Write-FixtureFile $InstallDir "Scripts\Custom suite\run.sh" "keep custom script"
    Write-FixtureFile $InstallDir "Data\Scripts\Obsolete suite\run.sh" "remove old suite"
    Write-FixtureFile $InstallDir "stale.dll" "remove old binary"
    Write-FixtureFile $stageDir "Data\Scripts\Example suite\run.sh" "echo updated"
    & $copyStep
    Assert-File "Data\Scripts\Example suite\run.sh" "echo updated"
    Assert-File "Scripts\Custom suite\run.sh" "keep custom script"
    foreach ($name in @("Config", "Connections", "Scripts", "Logs")) {
        Assert-File "$name\user-only.txt" "keep user data"
        Assert-File "$name\package-only.txt" "keep local version"
    }
    foreach ($relativePath in @("Data\Scripts\Obsolete suite", "stale.dll")) {
        if (Test-Path -LiteralPath (Join-Path $InstallDir $relativePath)) { throw "Stale package content survived: $relativePath" }
    }
    Write-Host "PASS: fresh install, update, root user data preservation, and stale package cleanup."
}
finally {
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedTestRoot.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolvedTestRoot) -notlike 'Jrm install test *') {
        throw "Refusing cleanup outside the test temp directory: $resolvedTestRoot"
    }
    if (Test-Path -LiteralPath $resolvedTestRoot) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
