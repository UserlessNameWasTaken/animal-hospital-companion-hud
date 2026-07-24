# Animal Hospital Team HUD

A tracking-free Windows coordination HUD for the Roblox game **Animal Hospital
(Anomaly)**. Up to four players can share room status, shifts, and coffee
timers through an authoritative WebSocket relay.

The experimental single-player Run HUD, movement tracking, and vision tools are
still being calibrated and are intentionally not included in this public
release.

## Install for Windows

No command prompt or development tools are required:

1. Open the repository's
   [Releases](https://github.com/UserlessNameWasTaken/animal-hospital-companion-hud/releases/latest).
2. Download **AnimalHospitalTeamHUD-Windows-x64.zip**.
3. Right-click the ZIP and select **Extract All**.
4. Open the extracted folder.
5. Double-click **AnimalHospitalTeamHUD.exe**.

Windows SmartScreen may show an **Unknown publisher** warning because this
personal project is not code-signed. Select **More info**, verify that the file
came from this repository, and choose **Run anyway** if you trust it.

Ordinary players only need the Team HUD ZIP.

The host downloads **AnimalHospitalTeamHost-Windows-x64.zip**, extracts it, and
double-clicks **Start Hosting.cmd**. This starts both the relay and Team HUD on
the matching local address. Keep the relay window open throughout the session.

## Use the Team HUD

Enter your display name, the relay server address, team code, and private key,
then select **Join**. The first player may select **Create** when connected to a
relay.

See [Team/README.md](Team/README.md) for interaction details, automatic
reconnection behavior, and the complete global `End` command table.

## Build from source

Developers need Windows 11 and the
[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

Double-click `Publish Windows Apps.cmd` to create:

- `dist\AnimalHospitalTeamHUD-Windows-x64.zip`
- `dist\AnimalHospitalTeamRelay-Windows-x64.zip`
- `dist\AnimalHospitalTeamHost-Windows-x64.zip`

Or start the projects directly:

```powershell
dotnet run --project Team\AnimalHospitalTeam.Relay --urls http://127.0.0.1:5188
dotnet run --project Team\AnimalHospitalTeam.Client
```

## Current relay limitations

The development relay keeps rooms in memory. Restarting it removes active
teams. Public Internet deployment should use HTTPS/WSS and add room expiration,
rate limiting, and appropriate operational monitoring.

**NOTE**
Public Deployments now use HTTPS / WSS and have room expiration, rate limits, and server life tracking to keep the relay running smoothly. Restarting the relay can still cause team servers to shut down, so the relay tab should be kept open at all times.

## Safety and scope

This is an external accessibility and coordination companion. It does not
modify the Roblox client, read process memory, inject code, automate gameplay,
or bypass game systems. Users remain responsible for following Roblox and
game-specific rules.
