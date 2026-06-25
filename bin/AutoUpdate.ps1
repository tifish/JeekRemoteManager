$ErrorActionPreference = "Stop"
$appName = "JeekRemoteManager"

if ($args.Count -eq 0) {
    Exit 1
}

$downloadUrls = @($args | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
if ($downloadUrls.Count -eq 0) {
    Exit 1
}

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

Add-Type -AssemblyName System.Net.Http

function Format-ByteSize {
    param([long]$Bytes)

    if ($Bytes -ge 1GB) {
        return "{0:N1} GB" -f ($Bytes / 1GB)
    }

    if ($Bytes -ge 1MB) {
        return "{0:N1} MB" -f ($Bytes / 1MB)
    }

    if ($Bytes -ge 1KB) {
        return "{0:N1} KB" -f ($Bytes / 1KB)
    }

    return "$Bytes B"
}

function Download-FileWithProgress {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$Destination,
        [int]$IdleTimeoutSeconds = 30
    )

    $client = $null
    $response = $null
    $stream = $null
    $file = $null
    $activity = "Downloading update package"

    try {
        $client = [System.Net.Http.HttpClient]::new()
        $client.Timeout = [TimeSpan]::FromSeconds(30)
        $client.DefaultRequestHeaders.UserAgent.ParseAdd("$appName-Updater/1.0")

        $response = $client.GetAsync(
            $Url,
            [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            throw "HTTP $([int]$response.StatusCode) $($response.ReasonPhrase)"
        }

        $totalBytes = $response.Content.Headers.ContentLength
        $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $file = [System.IO.File]::Open(
            $Destination,
            [System.IO.FileMode]::Create,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)

        $buffer = New-Object byte[] (1024 * 1024)
        [long]$received = 0
        $sw = [Diagnostics.Stopwatch]::StartNew()
        [long]$lastConsoleMs = -5000
        [int]$nextConsolePercent = 0

        while ($true) {
            $readTask = $stream.ReadAsync($buffer, 0, $buffer.Length)
            if (-not $readTask.Wait($IdleTimeoutSeconds * 1000)) {
                throw "No download data received for $IdleTimeoutSeconds seconds."
            }

            $read = $readTask.GetAwaiter().GetResult()
            if ($read -le 0) {
                break
            }

            $file.Write($buffer, 0, $read)
            $received += $read

            $elapsedSeconds = [Math]::Max($sw.Elapsed.TotalSeconds, 0.1)
            $speed = $received / 1MB / $elapsedSeconds
            $receivedText = Format-ByteSize $received

            if ($totalBytes -and $totalBytes -gt 0) {
                $percent = [Math]::Min(100, [Math]::Floor(($received * 100.0) / $totalBytes))
                $totalText = Format-ByteSize $totalBytes
                $status = "{0}% ({1} / {2}, {3:N1} MB/s)" -f $percent, $receivedText, $totalText, $speed
                Write-Progress -Activity $activity -Status $status -PercentComplete $percent

                if ($percent -ge $nextConsolePercent -or $sw.ElapsedMilliseconds - $lastConsoleMs -ge 5000) {
                    Write-Host "      $status"
                    $nextConsolePercent = [Math]::Min(100, $percent + 5)
                    $lastConsoleMs = $sw.ElapsedMilliseconds
                }
            } else {
                $status = "{0} downloaded, {1:N1} MB/s" -f $receivedText, $speed
                Write-Progress -Activity $activity -Status $status -PercentComplete 0

                if ($sw.ElapsedMilliseconds - $lastConsoleMs -ge 5000) {
                    Write-Host "      $status"
                    $lastConsoleMs = $sw.ElapsedMilliseconds
                }
            }
        }

        $file.Flush()
        if ($totalBytes -and $totalBytes -gt 0 -and $received -lt $totalBytes) {
            throw "Download ended early: $(Format-ByteSize $received) of $(Format-ByteSize $totalBytes)."
        }

        Write-Progress -Activity $activity -Completed
        Write-Host "      Downloaded $(Format-ByteSize $received) in $([Math]::Max(1, [int]$sw.Elapsed.TotalSeconds))s."
    } finally {
        Write-Progress -Activity $activity -Completed
        if ($file -ne $null) {
            $file.Dispose()
        }
        if ($stream -ne $null) {
            $stream.Dispose()
        }
        if ($response -ne $null) {
            $response.Dispose()
        }
        if ($client -ne $null) {
            $client.Dispose()
        }
    }
}

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
    $downloaded = $false
    for ($i = 0; $i -lt $downloadUrls.Count; $i++) {
        $downloadUrl = $downloadUrls[$i]
        Write-Host "      Mirror $($i + 1)/$($downloadUrls.Count): $downloadUrl"
        Remove-Item -Force -LiteralPath $packPath -ErrorAction SilentlyContinue

        try {
            Download-FileWithProgress -Url $downloadUrl -Destination $packPath
            $downloaded = $true
            break
        } catch {
            Write-Host "      Download failed from this mirror: $($_.Exception.Message)" -ForegroundColor Yellow
            Remove-Item -Force -LiteralPath $packPath -ErrorAction SilentlyContinue

            if ($i -lt $downloadUrls.Count - 1) {
                Write-Host "      Trying next mirror..."
            }
        }
    }

    if (-not $downloaded) {
        Write-Host "Download failed from all mirrors." -ForegroundColor Red
        Start-Sleep -Seconds 5
        Exit 1
    }

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
