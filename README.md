# Animal Hospital Team HUD

A tracking-free Windows coordination HUD for the Roblox game **Animal Hospital
(Anomaly)**. Up to four players can share room status, shifts, and coffee
timers through an authoritative WebSocket relay.

The experimental single-player Run HUD, movement tracking, and vision tools are
still being calibrated and are intentionally not included in this public
release.

## Requirements

- Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build from
  source
- Borderless-windowed Roblox is recommended

## Build double-clickable executables

Double-click `Publish Windows Apps.cmd`. It creates two self-contained Windows
applications:

- `dist\AnimalHospitalTeamHUD\AnimalHospitalTeam.Client.exe`
- `dist\AnimalHospitalTeamRelay\AnimalHospitalTeam.Relay.exe`

The published Team HUD does not require teammates to install .NET. Keep each
published folder together when copying or zipping it.

## Run from source

Start the relay:

```powershell
dotnet run --project Team\AnimalHospitalTeam.Relay --urls http://127.0.0.1:5188
```

Start up to four Team HUD clients:

```powershell
dotnet run --project Team\AnimalHospitalTeam.Client
```

The first player selects **Create** and shares the generated team code and
private key. Teammates enter unique names and join with those values.

See [Team/README.md](Team/README.md) for interaction details, automatic
reconnection behavior, and the complete global `End` command table.

## Current relay limitations

The development relay keeps rooms in memory. Restarting it removes active
teams. Public Internet deployment should use HTTPS/WSS and add room expiration,
rate limiting, and appropriate operational monitoring.

## Safety and scope

This is an external accessibility and coordination companion. It does not
modify the Roblox client, read process memory, inject code, automate gameplay,
or bypass game systems. Users remain responsible for following Roblox and
game-specific rules.
