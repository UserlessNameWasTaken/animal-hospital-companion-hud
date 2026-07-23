$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$distRoot = Join-Path $projectRoot 'dist'
$teamHudOutput = Join-Path $distRoot 'AnimalHospitalTeamHUD'
$relayOutput = Join-Path $distRoot 'AnimalHospitalTeamRelay'

function Publish-App {
    param(
        [Parameter(Mandatory = $true)][string]$Project,
        [Parameter(Mandatory = $true)][string]$Output
    )

    dotnet publish $Project `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --output $Output `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false

    if ($LASTEXITCODE -ne 0) {
        throw "Publishing failed for $Project"
    }
}

if (Test-Path -LiteralPath $distRoot) {
    Remove-Item -LiteralPath $distRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $distRoot | Out-Null

Publish-App `
    -Project (Join-Path $projectRoot 'Team\AnimalHospitalTeam.Client\AnimalHospitalTeam.Client.csproj') `
    -Output $teamHudOutput
Publish-App `
    -Project (Join-Path $projectRoot 'Team\AnimalHospitalTeam.Relay\AnimalHospitalTeam.Relay.csproj') `
    -Output $relayOutput

$teamInstructions = @'
ANIMAL HOSPITAL TEAM HUD
========================

1. Double-click AnimalHospitalTeamHUD.exe.
2. Enter your display name.
3. Enter the server address, team code, and private key shared by your host.
4. Select JOIN.

The first player may select CREATE when connected to a relay server.
Keep the private key within your team.
'@

$relayInstructions = @'
ANIMAL HOSPITAL TEAM RELAY
==========================

This package is for the person hosting the shared relay, not ordinary players.

For a local test, double-click Start Local Relay.cmd. The relay listens at:
http://127.0.0.1:5188

Closing the relay window ends the server and removes its in-memory teams.
Internet hosting requires HTTPS/WSS and additional operational protections.
'@

Set-Content -LiteralPath (Join-Path $teamHudOutput 'START HERE.txt') `
    -Value $teamInstructions -Encoding UTF8
Set-Content -LiteralPath (Join-Path $relayOutput 'START HERE.txt') `
    -Value $relayInstructions -Encoding UTF8

$relayLauncher = @'
@echo off
cd /d "%~dp0"
AnimalHospitalTeamRelay.exe --urls http://127.0.0.1:5188
pause
'@
Set-Content -LiteralPath (Join-Path $relayOutput 'Start Local Relay.cmd') `
    -Value $relayLauncher -Encoding ASCII

$teamZip = Join-Path $distRoot 'AnimalHospitalTeamHUD-Windows-x64.zip'
$relayZip = Join-Path $distRoot 'AnimalHospitalTeamRelay-Windows-x64.zip'
Compress-Archive -Path (Join-Path $teamHudOutput '*') -DestinationPath $teamZip -CompressionLevel Optimal
Compress-Archive -Path (Join-Path $relayOutput '*') -DestinationPath $relayZip -CompressionLevel Optimal

Write-Host ''
Write-Host 'Published:'
Write-Host "  Team HUD:   $teamHudOutput\AnimalHospitalTeamHUD.exe"
Write-Host "  Team Relay: $relayOutput\AnimalHospitalTeamRelay.exe"
Write-Host "  Player ZIP: $teamZip"
Write-Host "  Relay ZIP:  $relayZip"
