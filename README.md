# VVE Raid - Minimal Performance Patch

An unofficial, standalone Harmony patch for RimWorld 1.6 targeting an idle performance bottleneck in [Vanilla Vehicles Expanded - Tier 3 (Raid / VVE Raid)](https://steamcommunity.com/sharedfiles/filedetails/?id=3720926003).

---

## Disclaimer & Credits

- **Not an Official Mod**: This repository is an unofficial third-party fix and is not created, maintained, or endorsed by **tlupatac**, the **Vanilla Expanded team**, or any contributors to **Vanilla Vehicles Expanded**.
- **No Claim of Ownership**: All rights, code, textures, defs, trademarks, and intellectual property of **VVE Raid**, **Vanilla Vehicles Expanded**, and **RimWorld** belong to their respective original authors and **Ludeon Studios**.
- **Non-Destructive**: This patch leaves all original Workshop files completely untouched and does not redistribute any original mod assemblies or assets.
- **Context & Methodology**: I am just a busy software engineer who likes to relax with a game on the weekends without turning it into a second job or full-time hobby modding project. AI assistance was utilized to quickly help identify and isolate the exact IL hot path in the decompiled code, followed by human manual code review, IL verification, and in-game profiling with Dubs Performance Analyzer to validate the fix.

---

## The Problem

In the original `VVERaid.AI_Manager:MapComponentTick`, every single game tick executed:

```csharp
map.mapPawns.AllPawns.OfType<VehiclePawn>()
```

In RimWorld's core engine, `MapPawns.AllPawns` runs a recursive search (`ThingOwnerUtility.GetAllThingsRecursively`) across all containers, buildings, drop pods, and inventories on the map. On colonies with many pawns, animals, and storage structures, this caused continuous tick overhead (~0.5 ms – 1.0 ms / tick in Dubs Performance Analyzer) even when **zero** vehicles were on the map and no raid was active.

Immediately after that scan, the original code already checked `vehicle.Spawned`:

```csharp
if (vehiclePawn != null && vehiclePawn.Spawned && ...)
```

Scanning unspawned/container pawns every tick was redundant.

---

## What This Patch Does

This patch applies a minimal Harmony transpiler to `VVERaid.AI_Manager:MapComponentTick`:

- Replaces the first `callvirt Verse.MapPawns::get_AllPawns()` preceding `OfType<VehiclePawn>()` with `callvirt Verse.MapPawns::get_AllPawnsSpawned()`.
- `AllPawnsSpawned` directly accesses RimWorld's cached internal list (`pawnsSpawned`), dropping idle execution overhead from ~0.9 ms to < 0.001 ms per tick.
- **Unchanged Logic**: The second call to `AllPawns` (which feeds a separate `List<Pawn>` hunt query), all vehicle AI dispatching, timers, cleanup loops, and raid mechanics remain 100% identical to the original mod.
- **Failsafe**: Built-in MVID guards verify target assembly signatures on startup. If either RimWorld or VVE Raid updates and changes structure, the patch safely aborts without crashing the game.

---

## Load Order

1. `Harmony` (`brrainz.harmony`)
2. `Core`
3. `Vehicle Framework`
4. `Vanilla Vehicles Expanded`
5. `VVE Raid` (Original mod)
6. **`VVE Raid - Minimal Performance Patch`** (This mod)

---

## How to Install / Revert

### Installation

1. Place this folder into your RimWorld mods directory:
   `...\Steam\steamapps\common\RimWorld\Mods\VVERaid.PerformancePatch`
2. Enable the mod in RimWorld's mod menu after VVE Raid.
3. Check the debug log (`~`) for:
   `[VVE Raid Minimal Performance Patch] Applied: spawned-pawn vehicle scan only; AI and timers unchanged.`

### Removal / Reverting

- Simply disable or delete the mod folder.
- No custom Defs, MapComponents, or saved fields are added, making it safe to add or remove mid-save.

---

## Building from Source

Requires .NET SDK with .NET Framework 4.8 targeting pack.

```powershell
# Build and run verification test against game assemblies
dotnet build Tests\Verify.csproj -c Release
.\Tests\bin\Release\net48\VVERaid.PerformancePatch.Verify.exe "C:\Path\To\RimWorld" "C:\Path\To\Workshop\294100"
```
