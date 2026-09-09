# VRC FA26 SimHub Telemetry

Bridge the VRC Formula Alpha™ 2026's **private CAN/ECU channels** from Assetto Corsa (Custom
Shaders Patch) into [SimHub](https://www.simhubdash.com/), so data that normally only exists on
the in-car dashboard — Straight Mode, ERS deployment and harvesting, MGU-K power, PU thermals,
differential and brake maps, plank wear, the setting popups — can be shown on an external
dashboard or a secondary screen.

**League-safe by design:** the in-game side is read-only and never edits a car file, so it has
no checksum impact.

> This is not the FA25 plugin with a new name. The 2026 car renamed, dropped and added channels,
> and changed the shape of its CAN payload, so the 2025 build reads nothing from it. The two
> install side by side and don't interfere — different app folder, different DLL, different
> shared memory.

## Requirements

| | |
|---|---|
| Car | **`vrc_formula_alpha_2026_csp`** — the CSP ("Pro") build of the mod |
| CSP | 0.3.0-preview542 or newer (what the car itself requires) |
| SimHub | 9.x |
| OS | Windows |

The **vanilla (non-CSP) build of the car will not work.** It ships without the Lua scripts, so
there are no private channels to read. If you can't see the car's own digital dash in the
cockpit, you're on the wrong build.

## How it works

Two halves connected by a shared **memory-mapped file** (`Local\AcTools.CSP.VRC_FA26.v1`):

```
Assetto Corsa (CSP Lua app)                         SimHub (C# plugin)
  reads car CAN/ECU channels    ──►  MMF  ──►   reads MMF, exposes as
  writes a fixed C-struct                        SimHub properties → dashboard
```

- **`ingame-exporter/`** — a standalone CSP Lua app. Every frame it reads the car's private
  channel map and packs 126 values into a fixed-layout struct in shared memory. No car-file edits.
- **`simhub-plugin/`** — a SimHub `IDataPlugin` (net48) that maps that struct to ~190
  **VRC FA26 Telemetry** properties. Standard data (speed, RPM, gear, fuel, tyres) stays
  SimHub-native; this only adds what SimHub can't see.

## Install

### Option A — download (recommended, no build needed)

Grab both assets from the [latest release](../../releases/latest).

**1. In-game exporter** — `vrc_fa26_telemetry_ingame_app.zip`

1. Extract the zip into `…\assettocorsa\apps\lua\`. You should end up with
   `…\assettocorsa\apps\lua\vrc_fa26_telemetry\manifest.ini` (and the `.lua` next to it).
2. In game, with the FA26 loaded, open the apps sidebar and add the **VRC FA26 Telemetry**
   window.
3. Leave it open or minimised while you drive. It shows `connected: true` and a climbing
   counter when it's feeding data.

**2. SimHub plugin** — `VrcFa26Telemetry.dll`

1. Close SimHub if it's running.
2. Right-click the downloaded DLL → **Properties** → tick **Unblock** → OK.
   Windows marks downloaded DLLs as untrusted and SimHub will silently refuse to load them.
3. Copy the DLL next to `SimHub.exe` (default `C:\Program Files (x86)\SimHub`).
4. Start SimHub → it asks about the new plugin → enable it → restart SimHub.

### Option B — build from source

See [`simhub-plugin/README.md`](simhub-plugin/README.md). The in-game half is plain Lua —
copy `ingame-exporter/` to `apps/lua/vrc_fa26_telemetry/`, nothing to build.

## Checking it works

Get on track in the FA26, then in SimHub open any dashboard or the property picker and look
under **VRC FA26 Telemetry**:

- `Connected` = **1** and `Counter` climbing → the pipe is live.
- `Connected` = 0 → the in-game app isn't publishing. See Troubleshooting.

## Using the properties

Everything appears in the SimHub property picker grouped under **VRC FA26 Telemetry**. The
full list with notes is in [PROPERTIES.md](PROPERTIES.md). Highlights:

| Group | Examples |
|---|---|
| ERS | `KersChargePct`, `KersDeployMJ`, `KersRegenMJ`, `KersRegenRemainingMJ`, `SocDeltaPct` |
| MGU-K | `MgukPowerKW`, `MgukPowerBar` (±1, centre-out), `MgukMaxPower`, `IsPowerLimited` |
| Straight Mode | `SlmLatch` (0–3), `SlmLatchName`, `SlmActive` |
| Overtake / Boost | `IsOvertakeActive`, `IsOvertakeActivePending`, `IsBoostActive` |
| Power unit | `PuMode` (1–14), `PuModeName`, `PuTemperature`, `Strat` |
| Brakes | `BrakeBiasPct`, `BrakeBiasLivePct`, `BrakeMigrationPct`, `BrakeShapeMap` |
| Differential | `DiffEntry`, `DiffMid`, `DiffExit` |
| Tyres | `TyreTempDeltaFL…RR` (delta to optimum, as the cockpit shows it), `BrakeDiscTempFL…RR` |
| Fuel / stint | `FuelSavedLastLapG`, `FuelDeltaG`, `TargetLapTime`, `TargetStintLaps` |
| Wear | `PlankWear`, `FrontLegalityWearMm`, `MidLegalityWearMm`, `RearLegalityWearMm`, `IsPlankIllegal` |
| Popups | `SplashKind`, `SplashLabel`, `SplashValue`, `SplashColor`, `BannerKind` |
| Flags | `IsCaution` — not in AC's shared memory, so SimHub can't see it without this |

Raw values are sent unformatted. `*Pct`, `*Text` and `*Color` variants are there so a
cockpit-replica dashboard doesn't need to re-derive the car's own formatting.

## Troubleshooting

**`Connected` stays 0**
- You're in the vanilla build of the car, not `vrc_formula_alpha_2026_csp`.
- The app window was never added in the in-game apps sidebar.
- You're spectating or watching a replay — the exporter only reads the player's own car.

**Counter frozen at a number**
- The app window was closed. Some CSP builds only tick a Lua app while its window exists.
  Minimise it rather than closing it.

**No `VRC FA26 Telemetry` group in the property picker**
- The DLL wasn't unblocked, or the plugin wasn't enabled in SimHub → Settings → Plugins.
  Both are silent failures.

**`Connected` = 1 but a property reads 0 forever**
- VRC renamed or removed that channel in a car update. Channel names are resolved by name at
  runtime, so a rename shows up as a permanent zero rather than an error. Re-dump the channel
  map with the probe in `tools/channel-probe/` and open an issue with the output.

**An error in the log mentioning another VRC car's `can.lua`**
- Not from this project. If you have several VRC cars installed, their scripts share module
  names, and an older car can end up parsing a newer car's CAN payload. Nothing here reads or
  loads another car's scripts.

## After a car update

VRC can change channel names between releases. `tools/channel-probe/` is a read-only CSP app
that dumps the car's live channel map — name, controller index, and current value — to a text
file. Run it once after an update and diff it against the previous dump before assuming the
plugin is broken.

## Notes

This repository contains only my own original tooling. It does **not** include any VRC mod
assets, car data, or third-party content. It is not affiliated with or endorsed by the VRC
Modding Team.

## License

Public domain — [CC0 1.0](LICENSE). No rights reserved: use it, change it, ship it, sell it,
fork it without asking. No attribution required, though a link back is always welcome.

The dedication covers this tooling only. It grants no rights to the VRC Formula Alpha™ 2026
mod itself — you still need to own the car — and "VRC" and "Formula Alpha" remain the VRC
Modding Team's trademarks.
