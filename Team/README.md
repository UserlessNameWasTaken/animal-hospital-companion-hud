# Animal Hospital Team HUD

The Team fork contains:

- `AnimalHospitalTeam.Shared` — shared state and action protocol
- `AnimalHospitalTeam.Relay` — authoritative in-memory HTTP/WebSocket relay
- `AnimalHospitalTeam.Client` — tracking-free WPF HUD

## Local use

Ordinary players can download `AnimalHospitalTeamHUD-Windows-x64.zip` from the
repository's Releases page, extract it, and start `AnimalHospitalTeamHUD.exe`.
They do not need the .NET SDK or a command prompt.

Start the relay:

```powershell
dotnet run --project AnimalHospitalTeam.Relay --urls http://127.0.0.1:5188
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
not yet include room expiration, durable storage, rate limiting, or a packaged
public deployment. Internet deployment should use HTTPS/WSS rather than an
unencrypted public endpoint.
