# Paranoidal Launcher — instructions for agents

Read this file in full before you change anything, then the sections of `README.md` that cover the code you touch.
`README.md` (Russian, kept short) is the contract of every feature; this file adds the working rules, the decisions behind the
design and the plans. Reply to the user in the language they write in (the team speaks Russian). Code, comments and
these documents are in English; texts players see are RU/EN; `README.md` and `CHANGELOG.md` are Russian.

## 1. What it is

- One Windows exe for players of the Factorio modpack **Paranoidal** (repository
  `FactorioParanoidal/ParanoidalModpack`, branch `pre_release_2.0`; `AppInfo.Owner/RepoName/Branch`).
- It installs and updates the game's `mods\` from that branch, checks the game (Factorio **2.0.77** with the Space
  Age expansion files; `AppInfo.RequiredFactorio`) and starts it with `--mod-directory`.
- Players have no git, no tokens and no technical skills. "Put the exe next to the game and press one button" is
  the whole user story; every feature must keep it that way.
- Stack: C# for .NET Framework 4.8 WinForms (present in every Windows 10/11, nothing to install), compiled by Roslyn
  `csc.exe` from `build.ps1`, no project file, no NuGet. MinGit is embedded as a resource and unpacked on first start.
- Licence: MIT (`LICENSE`); MinGit is GPLv2 (`vendor/`).

## 2. File map

| Path | Role |
|---|---|
| `src/AppInfo.cs` | version (single source), repo/branch, required game, data folder, `Settings` (`settings.ini`), `MaskUser` |
| `src/Program.cs` | entry point, assembly attributes from `AppInfo` |
| `src/MainForm.cs` | the window: cards (build, Factorio, mods folder), buttons, tabs «Что нового» / «Изменённые моды» / «Журнал» |
| `src/Ui.cs`, `src/Dialogs.cs` | dark Factorio-like theme, buttons, cards; report and host-version dialogs |
| `src/Tr.cs` | RU/EN: `Tr.T("русский", "English")` at the place of use |
| `src/Sync.cs` | `SyncEngine`: status, update, «Проверить файлы», parallel blob download, extra mods, start preparation |
| `src/Git.cs` | runs the bundled git with an isolated environment, kill handle for the download watchdog |
| `src/GitHubApi.cs` | anonymous read-only REST: change list, file sizes for batching, short commit id → full; offline cache |
| `src/Recognize.cs` | recognising a build already in `mods\` by content (no network needed) |
| `src/Rollback.cs` | one step back/forward without internet (difference only) |
| `src/VersionLine.cs` | «Скопировать версию» line, parsing the host's line, `mod-settings.dat` reader/fingerprint |
| `src/Factorio.cs` | finding installations, game version and DLC check |
| `src/ModList.cs` | DLC entries of `mod-list.json` |
| `src/GameConfig.cs` | load cache keys in the `config.ini` the game reads; warning about `~` dependencies |
| `src/LoadDiag.cs` | load diagnostics from `factorio-current/previous.log` |
| `src/Report.cs` | «Отчёт о проблеме» ZIP |
| `tools/` | `Harness.cs` (console driver of the engine), `UiShot.cs` (dialogs to PNG), `snap.ps1`, `testdata/` |
| `docs/factorio-load-cache.md` | measured behaviour of the 2.0.77 load cache: the basis of `LoadDiag` and `GameConfig` |
| `assets/` | `splash.png` (logo), `gear.png` (icon source), embedded by `build.ps1` |
| `vendor/` | MinGit zip (checked by SHA-256 in `build.ps1`) and its licence |

## 3. Versions, build, release

- The rule is in `README.md` → «Версии» and is enforced by `build.ps1`: **every change that produces a different exe
  raises `AppInfo.Version`** (its only place) and adds a Russian section at the top of `CHANGELOG.md`
  (`## x.y.z — YYYY-MM-DD`). Patch for fixes and small features, minor for larger ones. Comments and documents do
  not change the exe and need no version.
- The build is deterministic: the same sources give the same bytes. `dist/build-info.txt` keeps the version and
  SHA-256 of the released exe; `build.ps1` refuses another exe under the same version (`-SameVersion` only while the
  version was not given to anyone). Use it to prove that a refactoring or a comment edit did not change the exe:
  build with `-Out obj/dev/ParanoidalLauncher.exe` and compare the hash.
- `dist/` and `obj/` are not in Git. A release = a GitHub Release `vX.Y.Z` of this repository with
  `ParanoidalLauncher.exe`, its SHA-256 and the `CHANGELOG.md` section as the notes.
- Requirements: Visual Studio or Build Tools with Roslyn, .NET Framework 4.8 reference assemblies.

## 4. Decisions you must not undo without the team

Each one was chosen after a real problem; the reasons are what you need when a "simpler" idea comes up.

1. **Git, not per-file HTTP.** The first version downloaded files one by one from `raw.githubusercontent.com`. With
   thousands of files it hit GitHub limits (429), and a broken run left `mods\` half old, half new. Git transfers a
   compressed pack and only switches files at the end. The launcher keeps a partial shallow clone with sparse checkout
   of `/mods/` (no second 1.2 GB copy in `.git`, only the difference is downloaded). Details: README «Как хранится
   папка модов».
2. **Download in parallel batches with a watchdog** (`Sync.cs`, ~24 MB per request): an early version shrank the batch
   after a failure and never grew it back, so the speed decayed over time. A stalled or sharply slowed request is
   killed and asked again; finished requests stay on disk, so a cancelled update continues.
3. **Byte-for-byte files** (`info/attributes`: `* -text`). Factorio compares mods byte for byte in multiplayer; a CRLF
   copy made by a git client with `autocrlf` caused «mod content differs» between players. Never let git normalise
   line endings in `mods\`.
4. **The launcher never writes into a mod** and the modpack is not changed for the launcher (recognition works by
   content fingerprint, not by a marker file in `zzzparanoidal`).
5. **Player files are not touched**: saves, `mod-settings.dat`, `player-data.json`, zip mods, mods the player chose to
   keep. Only the DLC entries of `mod-list.json` are set (quality and elevated-rails on, space-age off: the modpack
   is played without Space Age content) and only the three cache keys of the game `config.ini` (with one backup).
6. **One question, no jargon.** No branch names or git words in the window; an update asks once and shows what changes.
7. **Journal, `sync-log.txt`, `crash.txt` and the report stay Russian** in both interface languages: the team reads
   them. Default interface language: Russian for the 15 post-Soviet countries (UI language or region), else English.
8. **Report ≤ 20 MB** (the Discord upload limit), never `player-data.json` (Factorio account token), user name in paths
   masked. Save options: latest, latest autosave, chosen file; a save that does not fit is reported, not cut.
9. **Rollback keeps only the difference** of one step and goes back to the version the game was last *played* with;
   it works offline and «Вернуть» goes forward again. «Не менять версию» and «Версия хоста…» cover hosts that did not
   update yet, saves broken by an update and series on one version.
10. **Offline is a normal state**: «ИГРАТЬ», rollback, report, diagnostics and recognition must work without GitHub;
    network waits are short (20 s for the head check).
11. **Data next to the exe** (`ParanoidalLauncher-data`), relative paths inside the exe folder: a whole game folder can
    be copied to another computer or over the LAN and keeps working.
12. **The modpack's load cache.** The launcher turns the game's cache on before every start; any change of
    `mod-settings.dat` (also per-user settings changed in game) and any enabled mod with a `~` dependency make
    Factorio 2.0.77 rebuild the data stage. Facts and log markers: `docs/factorio-load-cache.md`.

## 5. Testing

- **Harness first.** `tools/build-harness.ps1` → `obj/Harness.exe` drives the real engine without the window (usage at
  the top of `tools/Harness.cs`). Always set `PARANOIDAL_LAUNCHER_DATA_DIR` to a test folder and use a test mods
  folder; `PARANOIDAL_LAUNCHER_TEST_OFFLINE=1` simulates no connection. Exact copies of build versions for a stand:
  `git archive <commit> mods | tar -x` from a clone of the modpack.
- `obj/Harness.exe --loaddiag-selftest tools/testdata/load-logs-2.0.77.zip` must print `SELFTEST OK` after any change
  of `LoadDiag.cs`/`GameConfig.cs`.
- Dialogs: `tools/build-uishot.ps1` renders them to PNG without showing them. Do not click the window with posted
  messages and never take the focus from a running game.
- **Never** use the player's real game folder, `%APPDATA%\Factorio`, saves, `mod-list.json` or `mod-settings.dat` for
  tests, and never kill a game you did not start. Real game launches (e.g. new reference logs after a game update)
  only with the owner's permission, in an isolated copy with its own write-data (`--config`), closed with WM_CLOSE.
- What only a person can accept: real clicks and dialogs, the diagnostics line after a real game exit, Steam installs,
  copying between two computers, default language on a non-Russian Windows. Say so in the handoff; do not claim them.
- When the game version changes: re-measure the cache markers (`docs/factorio-load-cache.md`), update
  `AppInfo.RequiredFactorio` and the reference logs.

## 6. Ideas not done yet

Proposed by agents and discussed with the team; none is approved as a plan. Ask before starting one.

- **Self-update** of the launcher from this repository's Releases (download, check SHA-256, rename the running exe to
  `.old`, restart). Considered the most valuable: without it fixes reach only players who download again.
- **First-start wizard**: find installations, copy the launcher into the game folder (copy and restart; a running exe
  cannot move itself), desktop shortcut via `IShellLink` and the real desktop path (OneDrive). If the game is in
  `Program Files`, stay where it is and only create the shortcut. Ask before deleting the original (antivirus).
- **Backup of saves** before an update of the build (mod migrations are one-way), with size and age limits.
- **Connect to a server** (`--mp-connect`) with a comparison of the host's version line before connecting.
- **Disk space check** before an update and before the first cached start (build ~1 GB, atlas cache ~1 GB).
- **Video memory hint** from `Dedicated video memory size` in the game log (the modpack needs ~3.2 GB at the highest
  settings) with suggested `video-memory-usage`/graphics settings.
- **Cache warm-up** after an update (`--create` into a temp file builds the data cache without a window); measure first.
- **Startup settings that differ from the build defaults**, with a reset (players change them and cannot join the host).
- After-start behaviour (tray / close / nothing), links to the modpack FAQ and Discord, starting Steam installs through
  Steam (`-applaunch 427520`) if direct start proves unreliable there.
- Embedding the modpack's illustrated guide (`FactorioParanoidal/guide-to-the-paranoidal`) or a link to it.

## 7. Working rules

- Read the code you change and its README section; keep README in sync with behaviour (it is the contract).
- Every message that a player sees goes through `Tr.T`; journal lines and exceptions follow §4.7.
- Do not add dependencies, installers or network services; GitHub anonymous access only (no tokens on players' PCs).
- Do not publish, tag, push or upload a Release without the owner's explicit command. Commit titles in Russian,
  third-person verb, no trailing period ("Исправляет …"); list only checks that were really done.
