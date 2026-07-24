# Animal Hospital Team HUD

The Team fork contains:

- `AnimalHospitalTeam.Shared` — shared state and action protocol
- `AnimalHospitalTeam.Relay` — authoritative in-memory HTTP/WebSocket relay
- `AnimalHospitalTeam.Client` — tracking-free WPF HUD

## Local use

Ordinary players can download `AnimalHospitalTeamHUD-Windows-x64.zip` from the
repository's Releases page, extract it, and start `AnimalHospitalTeamHUD.exe`.
They do not need the .NET SDK or a command prompt.

The team host can instead download `AnimalHospitalTeamHost-Windows-x64.zip`
and double-click `Start Hosting.cmd`. It launches the local relay on port 5188
and then opens the Team HUD.

Start the relay:

```powershell
dotnet run --project AnimalHospitalTeam.Relay --urls http://127.0.0.1:5188
```

For the hosted team service, configure the Cloudflare Tunnel origin/service as
`http://127.0.0.1:5188`. Players enter only the public base URL:
`https://relay.ahospitalhud.com`. Do not add `/health`, `/ws`, or port `5188`
to the public address.

The public hostname must be a published application route attached to the
tunnel. Its DNS record should point to the tunnel's
`<tunnel-id>.cfargotunnel.com` hostname. Do not create an A record pointing the
public hostname to `127.0.0.1`; that address always means the current user's
own computer.

With the relay and `cloudflared` running, double-click `Run Tunnel Test.cmd` to
verify public health, room creation, secure WebSockets, and synchronization
between two simulated clients. The .NET 8 SDK is required for this developer
test. You can test another deployment from a terminal with:

```powershell
dotnet run --project AnimalHospitalTeam.SmokeTests -- https://your-relay-hostname.example
```

Start up to four clients:

```powershell
dotnet run --project AnimalHospitalTeam.Client
```

The first player enters a display name and selects **Create**. Share the
generated team code and private key only with teammates. Other players enter a
unique name, the same server address, team code, and private key, then select
**Join**.

## Interaction and reconnection

- Choose **Safe**, **Anomaly**, **Clear**, or **Event**, then click a room.
- While the Team HUD is focused, `S`, `A`, `C`, or `E` selects an action and
  `1` through `8` applies it.
- Right-clicking a room toggles its event.
- **Always on top** keeps the window above borderless-windowed Roblox.
- The status dot is green while connected and red while offline.
- After a successful connection drops, retry delays are 1, 2, 4, 8, then at
  most 15 seconds. Unsent actions remain queued.
- Reconnecting with the same display name replaces that player's stale socket
  and immediately receives the authoritative state.
- **Leave** intentionally disconnects and cancels retries.

## Global `End` commands

These commands work while Roblox is foregrounded. The guide remains available
for six seconds. Sequence keys are consumed so they do not also trigger Roblox
actions.

| Sequence | Shared action |
| --- | --- |
| `End`, `1–8`, `PgUp` | Mark room safe |
| `End`, `1–8`, `PgDown` | Mark room anomalous |
| `End`, `1–8`, `Del` | Clear patient |
| `End`, `1–8`, `=` | Mark event active |
| `End`, `1–8`, `-` | Clear event |
| `End`, `1–8`, `Backspace` | Clear patient and event |
| `End`, `-` | Start small-coffee timer |
| `End`, `=` | Start tall-coffee timer |
| `End`, `\` | Start or finish shift |
| `End`, `PgUp` | Lock or unlock tall machine |
| `End`, `Del` | Clear every room |
| `End`, `]` | Reset rooms, shift clock, and coffee timers |
| `End`, `Backspace` | Start a new run at Shift 1 |
| `End`, `H` | Hide or restore Team HUD |

## Current relay limitations

The relay stores state in memory, so restarting it removes all teams. It does
not include durable storage, so restarting it removes active teams. Internet
deployment must use HTTPS/WSS rather than an unencrypted public endpoint.

## Relay safeguards

- Room creation uses a per-IP token bucket: three immediate creations, then
  one token per minute. Rejected requests return HTTP 429.
- At most 100 rooms may exist at once. Inactive rooms expire after six hours;
  rooms with connected players are not expired.
- WebSocket team secrets are sent in the `Authorization: Bearer` header, never
  in the URL query string.
- The relay trusts `CF-Connecting-IP` because its default listener is
  localhost-only. Do not expose the relay port directly to the Internet.

The defaults can be changed through ASP.NET Core configuration:

| Setting | Default |
| --- | ---: |
| `Relay__MaxRooms` | `100` |
| `Relay__RoomLifetimeSeconds` | `21600` |
| `Relay__CreateRoomTokenLimit` | `3` |
| `Relay__CreateRoomTokensPerPeriod` | `1` |
| `Relay__CreateRoomReplenishmentSeconds` | `60` |

The bearer-authentication change is intentionally incompatible with older
HUD builds. Deploy the updated relay and distribute the updated HUD together.
