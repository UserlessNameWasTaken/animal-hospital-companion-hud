$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$distRoot = Join-Path $projectRoot 'dist'
$runHudOutput = Join-Path $distRoot 'AnimalHospitalRunHUD'
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

New-Item -ItemType Directory -Force -Path $distRoot | Out-Null

Publish-App `
    -Project (Join-Path $projectRoot 'AnimalHospitalOverlay.csproj') `
    -Output $runHudOutput
Publish-App `
    -Project (Join-Path $projectRoot 'Team\AnimalHospitalTeam.Client\AnimalHospitalTeam.Client.csproj') `
    -Output $teamHudOutput
Publish-App `
    -Project (Join-Path $projectRoot 'Team\AnimalHospitalTeam.Relay\AnimalHospitalTeam.Relay.csproj') `
    -Output $relayOutput

$visionTools = Join-Path $runHudOutput 'tools'
$visionModel = Join-Path $runHudOutput 'dataset\analysis'
New-Item -ItemType Directory -Force -Path $visionTools, $visionModel | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'tools\live_location_probe.py') -Destination $visionTools
Copy-Item -LiteralPath (Join-Path $projectRoot 'tools\benchmark_location.py') -Destination $visionTools
Copy-Item -LiteralPath (Join-Path $projectRoot 'dataset\analysis\location_model.npz') -Destination $visionModel
Copy-Item -LiteralPath (Join-Path $projectRoot 'requirements.txt') -Destination $runHudOutput

Write-Host ''
Write-Host 'Published:'
Write-Host "  Run HUD:    $runHudOutput\AnimalHospitalOverlay.exe"
Write-Host "  Team HUD:   $teamHudOutput\AnimalHospitalTeam.Client.exe"
Write-Host "  Team Relay: $relayOutput\AnimalHospitalTeam.Relay.exe"
