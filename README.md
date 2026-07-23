# Animal Hospital Companion HUD

A Windows companion HUD for the Roblox game **Animal Hospital (Anomaly)**. It
tracks room status, shifts, and coffee timers without injecting into Roblox,
reading Roblox memory, or automating gameplay.

The repository contains two applications:

- **Run HUD** — personal HUD with manual controls, movement dead reckoning, and
  an optional low-frequency location candidate probe.
- **Team HUD** — tracking-free shared HUD for up to four players through an
  authoritative WebSocket relay.

## Requirements

- Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Borderless-windowed Roblox is recommended
- Python 3 with the packages in `requirements.txt` only if using the optional
  vision candidate probe

## Run HUD

Double-click `Launch Overlay.cmd`, or run:

```powershell
dotnet run --project AnimalHospitalOverlay.csproj
```

Commands are sequential, not simultaneous. Press `End`, then the remaining
keys within six seconds.

| Sequence | Action |
| --- | --- |
| `End`, `1–8`, `PgUp` | Mark patient safe |
| `End`, `1–8`, `PgDown` | Mark patient anomalous |
| `End`, `1–8`, `Del` | Clear patient |
| `End`, `1–8`, `=` / `-` | Set / clear room event |
| `End`, `1–8`, `Backspace` | Clear patient and event |
| `End`, `-` / `=` | Start small / tall coffee timer |
| `End`, `\` | Start or finish a shift |
| `End`, `]` | Reset the current HUD and timers |
| `End`, `PgUp` | Lock or unlock the tall coffee machine |
| `End`, `Backspace` | Start a new run |
| `End`, `Del` | Clear every room |
| `End`, `H` | Hide or restore the HUD |

Movement anchors, direction resets, calibration controls, and the experimental
vision observer are explained in the HUD itself. Tracking estimates never
change shared patient or event state automatically.

Local state is stored under
`%LOCALAPPDATA%\AnimalHospitalOverlay`. It is not stored in this repository.

## Team HUD

See [Team/README.md](Team/README.md) for relay and client instructions,
reconnection behavior, shared controls, and the full global keybind table.

The development relay keeps rooms in memory. Restarting it removes active
teams. Before using it over the public Internet, deploy it behind HTTPS/WSS and
add operational protections such as room expiration and rate limiting.

## Optional vision candidate probe

Install its Python dependencies:

```powershell
python -m pip install -r requirements.txt
```

The probe captures one downscaled desktop frame every two seconds only while
Roblox is foregrounded. Its result is diagnostic and never changes HUD state.
Raw gameplay captures and model-development artifacts are intentionally
excluded from version control; only the compact runtime model is included.

## Safety and scope

This is an external accessibility and coordination companion. It does not
modify the Roblox client, access process memory, inject code, simulate player
input, or bypass game systems. Users remain responsible for following Roblox
and game-specific rules.
