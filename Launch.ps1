# Launch JeekRemoteManager outside Grok Build's kill-on-close Job Object.
# Only Grok has this problem; Claude/Codex/other agents can use normal start.
# Win32_Process.Create is hosted by WMI and is not job-affiliated.
param(
    [string]$ExePath = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $PSScriptRoot "bin\JeekRemoteManager.exe"
}

$target = [IO.Path]::GetFullPath($ExePath)
if (-not (Test-Path -LiteralPath $target)) {
    throw "Missing executable: $target"
}

foreach ($process in Get-CimInstance Win32_Process -Filter "Name='JeekRemoteManager.exe'") {
    if ($process.ExecutablePath -and
        [IO.Path]::GetFullPath($process.ExecutablePath) -eq $target) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

$wd = Split-Path -Parent $target
$result = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
    CommandLine      = "`"$target`""
    CurrentDirectory = $wd
}

if ($result.ReturnValue -ne 0) {
    throw "Win32_Process.Create failed with code $($result.ReturnValue)"
}

Write-Host "Launched outside job: pid=$($result.ProcessId)"
exit 0
