# Velotext: one command to build, test and check coverage, locally and in CI.
# Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.
#
# Usage:
#   .\build.ps1                       Restore, build (warnings are errors), run tests, check core coverage.
#   .\build.ps1 -InstallSdk           Also install the .NET SDK from global.json into .dotnet (no admin needed).
#   .\build.ps1 -DesktopTests         Also run tests that use the real clipboard, hotkeys and keyboard hook.
#   .\build.ps1 -Publish -Version 1.0.0   Also publish a self-contained build and a Velopack installer to artifacts\.

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$InstallSdk,
    [switch]$DesktopTests,
    [switch]$Publish,
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.0',
    [double]$CoreCoverageMinimum = 95
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

function Invoke-Step([string]$Name, [scriptblock]$Command) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE." }
}

if ($InstallSdk) {
    $installer = Join-Path ([System.IO.Path]::GetTempPath()) 'dotnet-install.ps1'
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing

    # Run the installer script only when Microsoft's signature on it is valid.
    $signature = Get-AuthenticodeSignature $installer
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|, )O=Microsoft Corporation(,|$)') {
        throw "The .NET install script is not signed by Microsoft (status: $($signature.Status))."
    }

    $sdk = (Get-Content (Join-Path $root 'global.json') -Raw | ConvertFrom-Json).sdk.version
    & $installer -Version $sdk -InstallDir (Join-Path $root '.dotnet') -NoPath
}

Push-Location $root
try {
    Invoke-Step 'Restore' { dotnet restore Velotext.slnx }
    Invoke-Step 'Check formatting' { dotnet format Velotext.slnx --verify-no-changes --no-restore --severity warn }
    Invoke-Step 'Build' { dotnet build Velotext.slnx -c $Configuration --no-restore -p:Version=$Version }

    if ($DesktopTests) { $env:VELOTEXT_DESKTOP_TESTS = '1' }
    $results = Join-Path $root 'TestResults'
    if (Test-Path $results) { Remove-Item $results -Recurse -Force }
    $settings = Join-Path $root 'coverage.config'
    foreach ($project in 'Velotext.Core.Tests', 'Velotext.Platform.Windows.Tests', 'Velotext.App.Tests', 'Velotext.Conventions.Tests') {
        Invoke-Step "Test $project" {
            dotnet test --project "tests/$project/$project.csproj" -c $Configuration --no-build --results-directory $results -- --coverage --coverage-settings $settings --coverage-output-format cobertura --coverage-output "$project.cobertura.xml"
        }
    }

    $coreReport = Get-ChildItem $results -Recurse -Filter 'Velotext.Core.Tests.cobertura.xml' | Select-Object -First 1
    [xml]$coverage = [System.IO.File]::ReadAllText($coreReport.FullName)
    $core = $coverage.coverage.packages.package | Where-Object { $_.name -eq 'Velotext.Core' }
    $lineRate = [math]::Round([double]$core.'line-rate' * 100, 1)
    Write-Host "Velotext.Core line coverage: $lineRate % (minimum $CoreCoverageMinimum %)"
    if ($lineRate -lt $CoreCoverageMinimum) { throw "Core coverage $lineRate % is below $CoreCoverageMinimum %." }

    if ($Publish) {
        $publishDir = Join-Path $root 'artifacts\publish'
        $releaseDir = Join-Path $root 'artifacts\releases'
        Invoke-Step 'Publish' {
            dotnet publish src/Velotext.App/Velotext.App.csproj -c Release -r win-x64 --self-contained true -p:Version=$Version -p:PublishSingleFile=false -p:VelotextRelease=true -o $publishDir
        }
        Invoke-Step 'Restore tools' { dotnet tool restore }
        Invoke-Step 'Pack installer' {
            dotnet vpk pack --packId Velotext --packVersion $Version --packDir $publishDir --mainExe Velotext.exe --packTitle Velotext --packAuthors 'Velotext contributors' --icon src/Velotext.App/Assets/velotext.ico --outputDir $releaseDir --runtime win-x64
        }
        Write-Host "Installer: $releaseDir" -ForegroundColor Green
    }
}
finally {
    Pop-Location
}
