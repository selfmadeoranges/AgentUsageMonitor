[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$platformIoVersion = "6.2.0"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$firmwareDirectory = Join-Path $repositoryRoot "firmware\UsageMonitor.SmallTV"
$configPath = Join-Path $firmwareDirectory "include\SmallTvConfig.h"
$configExamplePath = Join-Path $firmwareDirectory "include\SmallTvConfig.example.h"
$virtualEnvironment = Join-Path $PSScriptRoot ".platformio-venv"
$platformIoPython = Join-Path $virtualEnvironment "Scripts\python.exe"
$firmwareBin = Join-Path $firmwareDirectory ".pio\build\smalltv-ultra\firmware.bin"

function Assert-NativeCommandSucceeded {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path -LiteralPath $platformIoPython)) {
    $pythonLauncher = Get-Command py -ErrorAction SilentlyContinue
    if ($pythonLauncher) {
        Write-Host "Creating the PlatformIO environment..."
        & $pythonLauncher.Source -3 -m venv $virtualEnvironment
    }
    else {
        $pythonLauncher = Get-Command python -ErrorAction SilentlyContinue
        if (-not $pythonLauncher) {
            throw "Python 3 is required. Install Python and run this script again."
        }

        Write-Host "Creating the PlatformIO environment..."
        & $pythonLauncher.Source -m venv $virtualEnvironment
    }

    Assert-NativeCommandSucceeded "Python virtual environment creation"
}

& $platformIoPython -m platformio --version *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Installing PlatformIO $platformIoVersion..."
    & $platformIoPython -m pip install --disable-pip-version-check "platformio==$platformIoVersion"
    Assert-NativeCommandSucceeded "PlatformIO installation"
}

if (-not (Test-Path -LiteralPath $configPath)) {
    Copy-Item -LiteralPath $configExamplePath -Destination $configPath
    Write-Host "Created an empty SmallTvConfig.h. Configure Wi-Fi through the recovery AP or edit the file before flashing."
}

Write-Host "Building the SmallTV-Ultra firmware..."
& $platformIoPython -m platformio run --project-dir $firmwareDirectory --environment smalltv-ultra --jobs 1
Assert-NativeCommandSucceeded "SmallTV firmware build"

if (-not (Test-Path -LiteralPath $firmwareBin)) {
    throw "The build completed without creating firmware.bin."
}

$firmwareFile = Get-Item -LiteralPath $firmwareBin
$firmwareHash = Get-FileHash -LiteralPath $firmwareBin -Algorithm SHA256

Write-Host ""
Write-Host "Build succeeded."
Write-Host "BIN: $($firmwareFile.FullName)"
Write-Host "Size: $($firmwareFile.Length) bytes"
Write-Host "SHA-256: $($firmwareHash.Hash)"
