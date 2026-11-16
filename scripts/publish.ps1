[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [switch]$SelfContained,

    [switch]$SingleFile,

    [switch]$Zip,

    [string]$Output
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\WowWallPaper\WowWallPaper.csproj'
$name = "WowWallPaper-$Runtime"

if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $root "dist\$name"
}

$outputPath = [System.IO.Path]::GetFullPath($Output)
$distRoot = [System.IO.Path]::GetFullPath((Join-Path $root 'dist'))
if (-not $outputPath.StartsWith($distRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to publish outside the dist directory: $outputPath"
}

if (Test-Path -LiteralPath $outputPath) {
    Remove-Item -LiteralPath $outputPath -Recurse -Force
}

$selfContainedValue = if ($SelfContained) { 'true' } else { 'false' }

$arguments = @(
    'publish', $project,
    '-c', 'Release',
    '-r', $Runtime,
    '--self-contained', $selfContainedValue,
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-o', $outputPath
)

if ($SingleFile) {
    $arguments += @(
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true'
    )
}
else {
    $arguments += '-p:PublishSingleFile=false'
}

Write-Host "Publishing $Runtime (self-contained=$selfContainedValue)"
dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$exe = Join-Path $outputPath 'WowWallPaper.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw "Published executable not found: $exe"
}

# Ship the project license and the third-party notices with every build.
foreach ($noticeName in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    $noticePath = Join-Path $root $noticeName
    if (-not (Test-Path -LiteralPath $noticePath)) {
        throw "Required notice file not found: $noticePath"
    }

    Copy-Item -LiteralPath $noticePath -Destination $outputPath -Force
}

$licensesSource = Join-Path $root 'licenses'
if (-not (Test-Path -LiteralPath $licensesSource)) {
    throw "License directory not found: $licensesSource"
}

Copy-Item -LiteralPath $licensesSource -Destination $outputPath -Recurse -Force

if ($Zip) {
    $zipPath = Join-Path $distRoot "$name.zip"
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $outputPath '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "Zip: $zipPath"
}

Write-Host "Publish complete: $exe"
