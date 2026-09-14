[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDir,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

# Publishes the Desktop app as a single self-contained exe (win-x64).
# Usage: .\tools\publish-desktop.ps1 [-Configuration Release] [-OutputDir <path>] [-Clean]

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\UsageMonitor.Desktop\UsageMonitor.Desktop.csproj'

if (-not $OutputDir) {
    $OutputDir = Join-Path $repoRoot 'artifacts\publish\UsageMonitor.Desktop'
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet SDK was not found on PATH. Install the .NET SDK and try again.'
}

if ($Clean -and (Test-Path $OutputDir)) {
    Write-Host "Removing $OutputDir"
    Remove-Item -Recurse -Force $OutputDir
}

Write-Host "Publishing $project ($Configuration) to $OutputDir"
& dotnet publish $project -c $Configuration -o $OutputDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$exe = Join-Path $OutputDir 'UsageMonitor.exe'
if (-not (Test-Path $exe)) {
    throw "Publish finished but $exe was not found."
}

$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ''
Write-Host "Done: $exe ($sizeMb MB)"
Write-Host 'Copy UsageMonitor.exe alone to deploy. The .pdb next to it is only for debugging.'
