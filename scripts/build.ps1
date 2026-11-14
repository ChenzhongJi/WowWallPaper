[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'WowWallPaper.slnx'

if (-not (Test-Path -LiteralPath $solution)) {
    throw "Solution not found: $solution"
}

Write-Host "Restoring $solution"
dotnet restore $solution

Write-Host "Building $Configuration"
dotnet build $solution -c $Configuration --no-restore

Write-Host ''
Write-Host "Build complete: $Configuration"
