$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
& (Join-Path $root 'build.ps1')

$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}

$app = Join-Path $root 'src\AdbDeviceVitals\bin\Release\AdbDeviceVitals.exe'
$testOutput = Join-Path $root 'artifacts\tests'
$testExe = Join-Path $testOutput 'ParserTests.exe'
New-Item -ItemType Directory -Force -Path $testOutput | Out-Null

& $compiler /nologo /target:exe /platform:anycpu /codepage:65001 `
    /out:$testExe `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:$app `
    (Join-Path $root 'tests\ParserTests.cs')

if ($LASTEXITCODE -ne 0) {
    throw "Test compilation failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath $app -Destination $testOutput -Force
& $testExe
if ($LASTEXITCODE -ne 0) {
    throw "Tests failed with exit code $LASTEXITCODE."
}

$renderExe = Join-Path $testOutput 'RenderSmokeTest.exe'
$renderPng = Join-Path $root 'artifacts\ui-smoke.png'
& $compiler /nologo /target:exe /platform:anycpu /codepage:65001 `
    /out:$renderExe `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:$app `
    (Join-Path $root 'tests\RenderSmokeTest.cs')

if ($LASTEXITCODE -ne 0) {
    throw "Render test compilation failed with exit code $LASTEXITCODE."
}

& $renderExe $renderPng
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $renderPng)) {
    throw 'Render smoke test failed.'
}
Write-Output "Rendered: $renderPng"

