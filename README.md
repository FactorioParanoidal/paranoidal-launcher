# Paranoidal Launcher

One exe for players: installs and updates `mods\` from the `pre_release_2.0` branch of
`FactorioParanoidal/ParanoidalModpack` with an embedded MinGit, checks the game (Factorio 2.0.77 Space Age)
and starts it with `--mod-directory`. Interface in Russian or English (`src/Tr.cs`); the history of changes is in
`CHANGELOG.md` (Russian, players read it).

**Agents and contributors:** start with `AGENTS.md` (working rules, decisions, plans). Knowledge base (read before
changing the related code):
- `docs/factorio-load-cache.md` — measured behaviour of the Factorio 2.0.77 load cache, exact log markers, causes of
  a cache miss, the `~` dependency bug; used by `src/LoadDiag.cs` and `src/GameConfig.cs`. Reference logs for tests:
  `tools/testdata/load-logs-2.0.77.zip`.
- The sections below are the contract of each feature; facts about git/GitHub found the hard way are marked **Fact**.

## Versions

- The version is set in **one place**: `AppInfo.Version` in `src/AppInfo.cs`. The window title, header, log,
  `crash.txt`, `sync-log.txt`, User-Agent, exe file/product version and the exe manifest all come from it
  (`Program.cs` assembly attributes reference the constant; `build.ps1` writes it into the manifest;
  `app.manifest` keeps a `0.0.0.0` placeholder). Do not write the version anywhere else.
- **Every change of the launcher that produces a different exe gets a new version** (patch `x.y.Z` for fixes and small
  features, minor `x.Y.0` for larger changes) and a section at the top of `CHANGELOG.md` in Russian (players read it).
- `build.ps1` enforces it:
  - `CHANGELOG.md` must start with `## <AppInfo.Version>`;
  - the build is deterministic, and `dist\build-info.txt` records the version and the exe SHA-256; building a
    different exe under the same version fails;
  - `-SameVersion` rebuilds the current version from changed code; use it only while that version was not given to
    anyone yet.
- A pure comment or documentation change produces the same exe and needs no new version.

## Build and test

- `powershell -ExecutionPolicy Bypass -File build.ps1` → `dist\ParanoidalLauncher.exe` + `dist\build-info.txt`.
  Needs Roslyn `csc.exe` (Visual Studio or Build Tools) and .NET Framework 4.8 reference assemblies.
  Development builds: `-Out obj/dev/ParanoidalLauncher.exe` (forward slashes from bash: backslashes are eaten) keeps
  `dist\` and its `build-info.txt` untouched.
- `tools\build-harness.ps1` → `obj\Harness.exe`, a console driver of the engine without WinForms (usage at the top of
  `tools\Harness.cs`): update / `--verify` / `--status` / `--rollback` / `--played` / `--info`, a commit id as the target
  (host's version), `--loaddiag`, `--loaddiag-selftest tools\testdata\load-logs-2.0.77.zip` (must print `SELFTEST OK`),
  `--report`, `--parse-host`, `--paths`. `-Src` builds it from another copy of `src\`, e.g. an older version to test a migration.
- `tools\build-uishot.ps1` → `obj\UiShot.exe <outDir> <ru|en> [factorioDir]`: draws the dialogs into PNG files without
  showing them. Posted mouse messages do not click WinForms buttons while another window (e.g. the player's game) covers
  the launcher, and a test must not take the focus from the user's game. `tools\snap.ps1` captures a visible window.
- Environment for tests: `PARANOIDAL_LAUNCHER_DATA_DIR` moves the launcher data (a test does not touch the player's
  settings and mods folder); `PARANOIDAL_LAUNCHER_TEST_OFFLINE=1` makes git (a refusing proxy) and the GitHub API fail
  as without a connection. A test stand needs exact copies of build versions: `git archive <commit> mods | tar -x` from
  the repository gives byte-exact files (checked by «Проверить файлы»).
- Launcher data (settings, MinGit, logs) live in `<exe folder>\ParanoidalLauncher-data` (`AppInfo.DataDir`; since 1.0.2).
  Fallback to the pre-1.0.2 `%LOCALAPPDATA%\ParanoidalUpdater` when the exe folder is not writable or is a mods folder
  (named `mods` or with `mod-list.json`: the sync would treat the data folder as a foreign mod); the reason goes to the log.
  First start next to the exe copies `settings.ini` from the old folder if there is none yet.
- `settings.ini` keys: `factorio`, `mods` (paths, relative to the exe folder when inside it — since 1.1.0, older absolute
  ones are migrated on start), `game-cache`, `pin` (1 = «Не менять версию»), `lang` (ru|en), `launch-build`,
  `launch-build-prev` (build of the last two game starts, for the load diagnostics), `load-diag-key` (the last analysed log).
  Other files in the data folder: `sync-log.txt`, `crash.txt`, `commits-cache.txt` (change list for offline),
  `load-diagnostics.txt` (history of the load diagnostics, ≤ 256 KB).

## Languages

- `Tr.T("русский", "English")` at the place of use; `Tr.Changed` re-applies the window texts (`MainForm.ApplyTexts`).
- Default: Russian when the Windows UI language is one of the 15 post-Soviet countries (ru, uk, be, kk, uz, ky, tg, tk,
  az, hy, ka, lt, lv, et, ro-MD) or the user's region/location is one of them; otherwise English (user, 2026-10-22).
- **The journal, `sync-log.txt`, `crash.txt` and the report stay Russian** in both languages (the team reads them):
  `ISyncUi.Stage(ru, en)` shows one and logs the Russian one, `SyncException(ru, en)` has `Message` (Russian, log) and
  `Display` (dialog). Commit messages from GitHub are not translated.

## How the mods folder is kept

- `<root>\mods` is the work tree, `<root>\.paranoidal-git` holds a partial shallow clone (no file contents kept after
  an update), sparse checkout `/mods/`. A folder next to `.git` (a developer's repository) is refused.
- `info/attributes` = `* -text`: files are compared and written byte for byte. Factorio compares mods byte for byte in
  multiplayer, while the repository's `.gitattributes` (`text=auto eol=lf`) would let git treat a CRLF copy as equal.
  1.0.0 compared bytes too, but only because that `.gitattributes` blob is never downloaded (partial clone, lazy fetch
  off); since 1.0.1 it does not depend on that.
- A full byte-for-byte check (fresh index, every file read again) runs on the first sync, on «Проверить файлы»
  and when the index is missing (an interrupted check). Otherwise git trusts the cached size and time of a file.
  There is no automatic one-time check for 1.0.0 installs: 1.0.0 already compared bytes (see above).
- Debugging this folder by hand: set `GIT_NO_LAZY_FETCH=1` like the launcher does. Without it a command that needs file
  contents (e.g. `ls-files --eol`) starts downloading the whole build blob by blob.
- **Fact:** in this partial clone a mixed `git reset <commit>` reads the object of every index entry whose id changes
  (also with `--no-refresh`), and file objects are not kept. `SyncEngine.ResetIndex` therefore drops the index and resets
  again when that fails (all entries are new then; git re-hashes the files on the next refresh). A plain `read-tree`
  would not need objects, but it loses the sparse-checkout bits: a later `reset --hard` would write the repository root
  (`.fallout/` etc.) next to `mods`.
- Never touched by git: `mod-list.json`, `mod-settings.dat`, zip mods, mods the player chose to keep.
  `ModList.EnsureDlc` sets only the DLC entries of `mod-list.json` before an update, a check and a game start:
  quality and elevated-rails on, space-age off (decision of the modpack team); the other entries stay as they are.

## Recognising a build that is already there (`src/Recognize.cs`)

Since 1.1.0. The modpack is not changed for this (a file in `zzzparanoidal` cannot hold the id of its own commit, a
hand-kept number gets forgotten, and the launcher must not write into a mod: multiplayer compares mods byte for byte).

- When the installed version is unknown (no `synced-commit`: a mods folder copied by hand, unpacked, shared over the LAN)
  or the build files differ from it (`StatusInfo.Modified` > 0), `CheckStatus` scans `mods\zzzparanoidal\info.json`:
  `factorio_version` 1.1 = the old build (Beta 8): only a warning, nothing is created or hashed.
- Otherwise it hashes the folder into its own index `.paranoidal-git\fp-index` (`update-index --info-only`: no file
  objects are written; sizes and times are kept, so only changed files are hashed next time; ~20 s for 950 MB the first
  time) and writes its trees into `.paranoidal-git\fp-objects` (never mixed with the build objects). The fingerprint is
  the tree id of `mods` (8 hex digits; zips, `mod-list.json`, `mod-settings.dat`, kept extras are excluded).
- Matching: the tree of every top-level entry is compared with the `mods` tree of every commit known locally
  (`cat-file --batch-all-objects`). Exact = every mod of that version is here byte for byte (foreign mods do not matter).
  The executable bit of a few files (e.g. `Kruise_Kontrol_Updated`, `100755`) is guessed locally, so the best candidates
  are re-checked with `diff-tree`, ignoring mode-only differences.
- Exact match → **adopt**: the recognition index becomes the index (no re-hashing), `reset` to that commit,
  `synced-commit` is written. No file of `mods\` changes. A rollback slot of the previous version is dropped (the files
  were changed outside the launcher). No exact match → the closest version and the differing mods are shown.
- Online and not recognised locally: `FetchHistory` downloads the commits of the last 120 days with their trees
  (`--shallow-since`, `--filter=blob:none`; ~2 MB / ~20 s the first time, ~620 commits; incremental later) into the
  normal object store, then matches again. These commits stay: a later offline recognition finds them too.
- **Fact:** GitHub accepts only `tree:0` among tree filters (`tree:2` → "tree filter allows max depth 0"), so the
  history cannot be limited to the top two tree levels. Do not fetch trees with a tree filter into the main store either:
  the "have" negotiation would then make the server skip trees the store lacks.
- First sync over a folder that is not recognised exactly: after the file list is known, `ISyncUi.ConfirmOverwrite`
  shows how many build files differ; «Отмена» leaves `mods\` untouched (`UpdateResult.Declined`). Only the difference
  is downloaded (the recognition index is copied as the first index).
- Portable copies: `.paranoidal-git` holds no absolute paths (git runs with `GIT_DIR`/`GIT_WORK_TREE`), the data folder
  is next to the exe, paths in `settings.ini` are relative inside the exe folder. A copied game folder is recognised
  offline at once (checked with a robocopy copy: 2 s).

## Offline

- `RemoteHead`: one `ls-remote` limited to 20 s, a second attempt only for errors that are not about the connection
  (`IsNetworkError`). `StatusInfo.Offline` → «Нет связи с GitHub», «ИГРАТЬ» stays available when a build is present
  (also an unrecognised one), the change list comes from `commits-cache.txt`.
- An update/download gives up quickly without a connection: «Could not resolve host» at once, three dead connections
  in a row in the parallel download. «Проверить файлы», rollback, report, load diagnostics, recognition and the
  `mod-list.json`/`config.ini` changes work offline.

## Rollback and «Не менять версию» (`src/Rollback.cs`)

- Before `reset --hard NEW` of an update `OLD → NEW` (not on the first sync, «Проверить файлы» or a missing index), the
  files of `OLD` that `NEW` changes or deletes are copied to `.paranoidal-git\rollback\mods\…`, the files `NEW` adds are
  listed (`slot.txt`: `target`, `cur`, `saved`, `added`, `missing`). Only the difference, never the whole build.
- The slot leads back to the version the game was last started with (`played-commit`, written by «ИГРАТЬ»): when `OLD`
  was never played (two updates in a row), the slot keeps its target and only adds what it lacks. Files changed by hand
  before the update are `missing` (not restorable offline: «Проверить файлы» fixes them).
- Rollback works offline and is an exchange: the current files go to a new slot, so «Вернуть» goes forward again.
  The index is rebuilt through the recognition index, then `ResetIndex`. Checked byte for byte with «Проверить файлы»
  offline in both directions and for the two-updates case (harness runs of 1.1.0 on exact copies of four commits).
- «Не менять версию» (`pin`): «ОБНОВИТЬ» is disabled, «ИГРАТЬ» does not offer the update; «Проверить файлы» checks the
  kept version. «Версия хоста…» parses the host's «Скопировать версию» line (`VersionLine.ParseHost`: the id in
  parentheses, 7–40 hex digits, either language), resolves a short id with the GitHub API, updates to that commit (only
  the difference) and turns the pin on.

## Load diagnostics (`src/LoadDiag.cs`)

- After the game exits (a 3 s timer watches `factorio.exe`) and at start (if the game is not running) the launcher
  parses `factorio-current.log` and `factorio-previous.log` of the game's data folder (`GameConfig.ResolveWriteData`:
  `[path] write-data` of the config.ini the game reads) and shows one line in the game card: time to the menu, data cache
  and atlas cache ✔/✖ and the first cause; the tooltip and the journal have all causes. Rules and markers:
  `docs/factorio-load-cache.md`. A new log (its first line) is logged once and appended to `load-diagnostics.txt`.

## Problem report (`src/Report.cs`)

- ZIP on the desktop, ≤ 20 MB (Discord, user 2026-10-22): `version.txt` (version line, state, game, paths, Windows),
  game logs, game `config.ini`, `mod-list.json`, `mod-settings.dat`, mods not from the build, changed build files,
  launcher journal of the session, `sync-log.txt`, `crash.txt`, `settings.ini`, load diagnostics history, fresh `*.dmp`
  of the game (≤ 3 days, while they fit). Optional save: the latest manual one, the latest `_autosave*`, or a chosen file;
  stored uncompressed and only if the whole archive stays under the limit, otherwise its path and size are reported.
- Never: `player-data.json` (Factorio account token). Text files go through `AppInfo.MaskUser` (profile folder →
  `%USERPROFILE%`, other `C:\Users\<name>` and the bare user name → `%USERNAME%`).

## Game config.ini (load cache)

- Before every game start (option «Кэш загрузки игры», on by default, `game-cache` in `settings.ini`) `GameConfig.EnableCache`
  sets `cache-prototype-data` ([other]), `cache-sprite-atlas` and `compress-sprite-atlas-cache` ([graphics]) to `true`.
- The file is the one the game reads without `--config` (the launcher never passes it): `config-path.cfg` of the game
  (`__PATH__executable__`, `__PATH__system-write-data__` = `%APPDATA%\Factorio`); without that file — `%APPDATA%\Factorio`.
- Only these keys change: an active line is set to `true`, otherwise a commented `; key=…` line is replaced, otherwise the
  key is added after the section header. BOM and line endings are kept. One backup `config.ini.paranoidal-backup`
  before the first change. A missing config.ini is created with only these keys; the game generates the rest.
- Skipped while Factorio runs: it rewrites config.ini on exit.
- `GameConfig.TildeMods` warns about enabled mods with a `~` dependency: Factorio 2.0.77 then never reuses the data
  stage cache (the modpack's `docs/Mod-Patches.md`, Angels entry; `docs/factorio-load-cache.md`).

## License

MIT (`LICENSE`). The embedded MinGit is GPLv2: `vendor/README.md`.
