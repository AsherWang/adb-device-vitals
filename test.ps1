$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
& (Join-Path $root 'build.ps1')

$dotnet = $null
$candidate = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (Test-Path -LiteralPath $candidate) {
    $sdks = @(& $candidate --list-sdks 2>$null)
    if ($LASTEXITCODE -eq 0 -and $sdks.Count -gt 0) { $dotnet = $candidate }
}
if (-not $dotnet) { $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source }
if (-not $dotnet) { throw '.NET 10 SDK not found.' }

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

# Pure logic tests: links the service/parser sources into a standalone console app.
$testOutput = Join-Path $root 'tests\bin\Release\net10.0-windows10.0.19041.0'
& $dotnet build (Join-Path $root 'tests\AdbDeviceVitals.Tests.csproj') -c Release -tl:off | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Test project build failed with exit code $LASTEXITCODE." }
& $dotnet (Join-Path $testOutput 'ParserTests.dll')
if ($LASTEXITCODE -ne 0) { throw "Parser tests failed with exit code $LASTEXITCODE." }

# Render smoke: real WPF windows with sample data, three PNG screenshots.
$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
# New names avoid stale WinForms screenshots being mistaken for fresh output.
$mainPng = Join-Path $artifacts 'wpf-main.png'
$settingsPng = Join-Path $artifacts 'wpf-settings.png'
$smsPng = Join-Path $artifacts 'wpf-sms.png'

& $dotnet build (Join-Path $root 'tests\AdbDeviceVitals.RenderTest.csproj') -c Release -tl:off | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Render project build failed with exit code $LASTEXITCODE." }
& $dotnet (Join-Path $testOutput 'RenderSmokeTest.dll') $mainPng $settingsPng $smsPng
if ($LASTEXITCODE -ne 0) { throw "Render smoke test failed with exit code $LASTEXITCODE." }
foreach ($png in @($mainPng, $settingsPng, $smsPng)) {
    if (-not (Test-Path -LiteralPath $png)) { throw "Missing render output: $png" }
}

Write-Output "Rendered: $mainPng, $settingsPng, $smsPng"
