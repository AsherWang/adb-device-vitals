$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\AdbDeviceVitals\AdbDeviceVitals.csproj'
$output = Join-Path $root 'src\AdbDeviceVitals\bin\Release'
$exe = Join-Path $output 'AdbDeviceVitals.exe'

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
    'publish', $project, '-c', 'Release', '-o', $output, '-tl:off',
    '-p:PublishSingleFile=true',
    '-p:SelfContained=false',
    '-p:DebugType=none',
    '-p:PublishReadyToRun=false'
)
& $dotnet @publishArgs

if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
if (-not (Test-Path -LiteralPath $exe)) { throw "Publish succeeded but $exe was not created." }

$version = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
Write-Output "Built: $exe (version $version)"
