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

Write-Host ''
Write-Host 'Published:'
Write-Host "  Team HUD:   $teamHudOutput\AnimalHospitalTeam.Client.exe"
Write-Host "  Team Relay: $relayOutput\AnimalHospitalTeam.Relay.exe"
