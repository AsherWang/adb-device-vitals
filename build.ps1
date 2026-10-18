$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\AdbDeviceVitals'
$output = Join-Path $project 'bin\Release'
$exe = Join-Path $output 'AdbDeviceVitals.exe'
$icon = Join-Path $project 'Resources\AdbDeviceVitals.ico'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}

if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'The .NET Framework C# compiler was not found.'
}

New-Item -ItemType Directory -Force -Path $output | Out-Null
$sources = Get-ChildItem -LiteralPath $project -Filter '*.cs' -Recurse |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    ForEach-Object { $_.FullName }

$iconArgument = '/win32icon:' + $icon
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 $iconArgument `
    /out:$exe `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.IO.Compression.dll `
    /reference:System.IO.Compression.FileSystem.dll `
    /reference:System.Windows.Forms.dll `
    $sources

if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

Write-Output "Built: $exe"

