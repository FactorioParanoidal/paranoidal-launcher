# Factorio 2.0.77 load cache: measured behaviour and log markers

Knowledge base for the launcher's load diagnostics (`src/LoadDiag.cs`) and the «Кэш загрузки игры» option
(`src/GameConfig.cs`). Measured on 2026-10-21/22 with the real modpack; the numbers below are facts of those runs, not
guesses. Re-measure when the game version changes (`AppInfo.RequiredFactorio`).

## Sources

- An isolated stand: a physical copy of the game and of the modpack `mods`, own write-data via `--config`, one GUI
  launch at a time (own PID, 180 s limit, the window closed with WM_CLOSE after `Factorio initialised`, never killed),
  `mod-settings.dat` edited between runs by a small script. The player's `%APPDATA%\Factorio` was never used.
- The `~` dependency bug: an earlier stand of 2026-10-21 (two launches with a `~` mod, bisection of the Angels mods).
- Copies of all logs for tests: `tools/testdata/load-logs-2.0.77.zip` (runs 01–11, `12/13-tilde-*` from the earlier
  stand, `results.jsonl` with the measured times; the user folder in paths is replaced by `user`). The harness reads
  them (`--loaddiag-selftest`).
- Engine: GOG `Factorio 2.0.77 (build 84539, win64, full, space-age)`.

## Times to the main menu (`Factorio initialised`), full modpack, cache keys on

| # | Change before the launch | To menu, s | Data stage | Atlas |
|---|---|---|---|---|
| 1 | cold: empty write-data | 105.4 | `not used` → `cached in` | `Crop bitmaps.` → `were not loaded` → `Atlas cache saved` |
| 2 | nothing | 44.7 | `loaded` | `were not loaded` → saved (always on the 2nd launch after a crop) |
| 3 | nothing | 22.7 | `loaded` | `loaded from disk cache` |
| 4 | one **runtime-per-user** mod setting | 66.7 | `loaded` → `Rebuilding data stage cache because mod settings changed.` → `cached in` | loaded |
| 5 | one **startup** mod setting | 66.6 | same as 4 | loaded |
| 6 | `graphics-quality=normal` — invalid value (2.0.77 accepts `high`, `medium`), ignored | 22.7 | loaded | loaded |
| 7 | `graphics-quality=medium` | 39.5 | loaded | `were not loaded` → saved (no reason line) |
| 8 | one line appended to a Lua file of a mod, version unchanged | 62.0 | `not used` (no reason) | loaded |
| 9 | an extra enabled mod not from the build | 60.8 | `not used` (no reason) | loaded |
| 10 | another installation (copy of bin+data) with the same write-data | 61.9 | `not used` (no reason) | loaded |
| 11 | `cache-prototype-data=false`, `cache-sprite-atlas=false` | 74.5 | no cache line at all | no atlas cache line |
| tilde | any enabled mod with a `~` dependency (reused 2026-10-21 runs) | — | `not used` on every launch, no reason | — |

The game rewrote neither `mod-settings.dat` nor `config.ini` on exit in these runs.

## Exact log markers (2.0.77)

- Load time: timestamp (first column, seconds since start) of `Factorio initialised`. Missing = the game was closed or
  crashed before the menu.
- Data stage:
  - `Data stage cache loaded in: X seconds.` — the file was read. **Not a hit by itself**: when
    `Rebuilding data stage cache because <reason>.` follows, the data stage runs anyway (seen reason: `mod settings changed`,
    for any section of `mod-settings.dat`, also per-user settings changed in game).
  - `Data stage cache not used.` — rejected before loading. The game never says why (also with `--verbose`).
  - `Data stage cached in: X seconds.` — a new cache was written, i.e. the data stage was executed.
  - Hit = `loaded` without a later `cached in`. No `Data stage cache` line at all = data cache is off.
- Atlas: `Atlases loaded from disk cache in X seconds.` (hit), `Atlases were not loaded from disk cache.` (miss, no reason),
  `Atlas cache saved in X seconds.`, `Crop bitmaps.` (crop cache built; the next launch misses the atlas once more).
  No atlas line = atlas cache is off.
- Lines present in every log (cold or warm) that allow a diff with the previous log: `Checksum of <mod>: N` for every
  enabled mod (graphics-only mods have `0`), first line `Factorio <version> (build …)`, `Program arguments:` (shows
  `--mod-directory`), `Read data path:`, `Binaries path:`, `Write data path:`, `Graphics options: […]` + its continuation line.
  `Loading mod <name> <version>` lines appear only when the data stage runs; a Lua `mods` table dump seen in some logs comes
  from a mod, not from the engine — do not rely on it.

## Cause of a miss (what the launcher reports)

| Observed in the current log | Probable cause |
|---|---|
| `Rebuilding … because mod settings changed.` | mod settings changed (any section, incl. per-user ones changed in game) |
| `not used` + the launcher updated/rolled back the build since the previous launch | the build changed |
| `not used` + set of `Checksum of` mods or one checksum differs from the previous log | those mods were added/removed/changed (name them) |
| `not used` + `Binaries path` / version line differs | another installation / game version uses the same data folder (one shared cache) |
| `not used` + `--mod-directory` differs | the game was started with other mods in between (e.g. without the launcher) |
| `not used` + everything identical + an enabled mod with `~` | the `~` bug (see below) |
| `not used` + everything identical, no `~` | cache deleted/damaged, or a mod changed without a Lua change; unknown |
| atlas miss + `Crop bitmaps.` in the previous log | normal second launch after new graphics; the next one is fast |
| atlas miss + `Crop bitmaps.` in this log | new/changed graphics (first launch after a change) |
| atlas miss + `Graphics options` differ | graphics settings changed |
| no cache lines | cache is off in `config.ini` |

Not measured: switching back to the first installation (expected another miss: one `data-cache.dat` per write-data),
Steam, `texture-streaming` (on in the user's config, off on the stand), other `Rebuilding … because …` reasons, a launch
interrupted during loading, a headless run between two GUI runs (then `factorio-previous.log` is the headless one).

## The `~` dependency bug

A dependency with the `~` prefix is stored in `data-cache.dat` as a plain required one; on the next start the game compares
it with `info.json`, sees a difference and drops the cache. One enabled mod with `~` is enough (reproduced with an empty mod
with `~ base` / `~ quality`; `?`, `(?)` and plain dependencies are fine). The modpack removes `~` from Angels
(`ParanoidalModpack-master/docs/Mod-Patches.md`); `GameConfig.TildeMods` warns about any enabled mod that still has one.
