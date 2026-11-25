$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\AdbDeviceVitals\AdbDeviceVitals.csproj'
$output = Join-Path $root 'src\AdbDeviceVitals\bin\Release'
$exe = Join-Path $output 'AdbDeviceVitals.exe'
$stage = Join-Path $root 'src\AdbDeviceVitals\obj\local-publish'

# The system dotnet.exe can be a host with no SDK; prefer a user-local SDK that actually has one.
$dotnet = $null
$candidate = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (Test-Path -LiteralPath $candidate) {
    $sdks = @(& $candidate --list-sdks 2>$null)
    if ($LASTEXITCODE -eq 0 -and $sdks.Count -gt 0) { $dotnet = $candidate }
}
if (-not $dotnet) { $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source }
if (-not $dotnet) { throw '.NET 10 SDK not found. Install it or add dotnet to PATH.' }

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

New-Item -ItemType Directory -Force -Path $output | Out-Null

# Framework-dependent single file: still one runnable exe, so the self-update script is unchanged.
# The target machine needs Microsoft Windows Desktop Runtime 10.
$publishArgs = @(
    'publish', $project, '-c', 'Release', '-o', $stage, '-tl:off',
    '-p:PublishSingleFile=true',
    '-p:SelfContained=false',
    '-p:DebugType=none',
    '-p:PublishReadyToRun=false'
)
$buildLock = [Threading.Mutex]::new($false, 'Local\AdbDeviceVitals.Build')
$locked = $false
try {
    try { $locked = $buildLock.WaitOne() }
    catch [Threading.AbandonedMutexException] { $locked = $true }

    & $dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
    $builtExe = Join-Path $stage 'AdbDeviceVitals.exe'
    if (-not (Test-Path -LiteralPath $builtExe -PathType Leaf)) { throw "Missing published file: $builtExe" }

    $sessionId = (Get-Process -Id $PID).SessionId
    $running = @(Get-Process -Name AdbDeviceVitals -ErrorAction SilentlyContinue |
        Where-Object { $_.SessionId -eq $sessionId })
    foreach ($instance in $running) {
        $exitSignal = $null
        if ([Threading.EventWaitHandle]::TryOpenExisting("Local\AdbDeviceVitals.Exit.$($instance.Id)", [ref]$exitSignal)) {
            try { [void]$exitSignal.Set() } finally { $exitSignal.Dispose() }
            [void]$instance.WaitForExit(10000)
        }
        # Older tray builds and unresponsive instances lack a working exit signal.
        if (-not $instance.HasExited) {
            $instance.Kill()
            if (-not $instance.WaitForExit(10000)) { throw "Cannot close PID $($instance.Id)." }
        }
    }

    try {
        if (Test-Path -LiteralPath $exe) {
            [IO.File]::Replace($builtExe, $exe, (Join-Path $stage 'AdbDeviceVitals.previous.exe'))
        } else {
            [IO.File]::Move($builtExe, $exe)
        }
    } finally {
        if ($running.Count -gt 0 -and (Test-Path -LiteralPath $exe)) {
            $started = Start-Process -FilePath $exe -WorkingDirectory $output -PassThru
            if ($started.WaitForExit(2000)) { throw "App exited during startup (code $($started.ExitCode))." }
            Write-Output "Running: $exe (PID $($started.Id))"
        }
    }
    $version = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
    Write-Output "Built: $exe (version $version)"
} finally {
    if ($locked) { $buildLock.ReleaseMutex() }
    $buildLock.Dispose()
}
