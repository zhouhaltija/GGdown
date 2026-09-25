# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

GGdown（前身为 GalleryGUI）— a WinUI 3 desktop app (zh-CN UI) that drives a bundled Python engine (gallery-dl used as a library) to batch-download media from Twitter/X and Pixiv. GPL-3.0; bundles GPL-2.0 gallery-dl, so installer ships license assets. A rename GalleryGUI → GGdown is in progress on the `dev` branch — use GGdown names for anything new.

Comments, commit messages (conventional-commit style, e.g. `feat: ...`), and UI strings are in Chinese — match that.

## Commands

```powershell
# Dev run: builds Debug x64 and launches GGdown.exe (uses repo engine\runner.py + PATH python)
pwsh scripts/run.ps1               # add -NoBuild to reuse the last build

# C# tests (xunit + Xunit.SkippableFact; E2E/engine tests auto-skip if no python / gallery-dl source)
dotnet test src\GGdown.sln
dotnet test src\GGdown.Tests\GGdown.Tests.csproj --filter "FullyQualifiedName~JsonlParserTests"

# Python engine tests (pytest; requires gallery_dl importable by system python)
python -m pytest engine/tests

# Full release build: engine assembly → dotnet publish → license assets → Inno Setup installer
pwsh scripts/build.ps1             # -SkipInstaller to stop before ISCC
pwsh scripts/build-engine.ps1      # engine only → dist\engine

# Migrations
dotnet ef migrations add <Name> --project src\GGdown.Core --startup-project src\GGdown.Core
```

Build the app with `-p:Platform=x64` (csproj rewrites AnyCPU→x64, but be explicit when invoking `dotnet build` on the csproj directly). Inno Setup 6/7 must be installed for `build.ps1`.

## Architecture

Three layers connected by a JSONL process protocol:

1. **`src/GGdown.App`** — WinUI 3 single-window shell (`MainWindow` + `Views/{Users,Downloads,History,Settings}Page`). No logic here; pages resolve services via `App.Current.Services`, VMs live in Core. WinUI specifics that matter: pages use `NavigationCacheMode=Enabled` (construct once), dialogs need `App.Current.MainWindow.DialogXamlRoot`, and `App.Readiness` gates first queries until startup DB init/recovery completes.

2. **`src/GGdown.Core`** — everything non-UI:
   - `DependencyInjection.AddGGdownCore(IAppPaths)` is the composition root; registration order and lifetimes are deliberate (see comments — e.g. `AddDbContextFactory` before `AddDbContext` so options are singleton; VMs depending on Scoped services are Transient, not singleton, to avoid captive dependencies).
   - **Data**: EF Core + SQLite. DB and all user data live under `%LOCALAPPDATA%\GGdown` (`Paths/AppPaths.cs`); `DataRootMigration` handles the legacy GalleryGUI folder/db rename. Migrations are applied by `DbInitializer` at startup (not `dotnet ef database update`).
   - **Engine**: `IDownloadEngine` → `RunnerEngine` spawns `python runner.py <cmd>` per operation, parses JSONL events from stdout (`Engine/JsonlParser.cs`), diagnostics from stderr. Protocol v1: events `hello/account/user/file-start/file-skip/file-done/url-start/job-done/end/fatal`; cancellation kills the process.
   - **Queue**: `DownloadQueueService` (singleton, DB access only via `IDbContextFactory`) runs jobs at a persisted concurrency, recovers Running/Pending → Failed on startup, aggregates per-task download counts via `StatsAggregator`.
   - **Sites**: `Sites/SiteRegistry` + `ISiteProvider` (Twitter, Pixiv) define per-site metadata, download-plan building, and cookies handling on the C# side; the engine has the mirror-image Python side.

3. **`engine/`** — Python. `runner.py` (CLI: hello/whoami/list-following/user-info/download) wraps **gallery-dl as a library** (no subprocess), emitting events on stdout. `sites/__init__.py` is the shared base (event emission, UTF-8 stdio fixups for Windows GBK, proxy-from-env, auth-error classification); `sites/{twitter,pixiv}.py` implement site specifics. Downloads swap gallery-dl's output object for a counting `EventOutput` and count failures by bridging ERROR-level logs.

### Engine location (two modes)

- **DEBUG**: `App.xaml.cs` `ApplyDevEngineOverrides` points at repo `engine\runner.py` + `python` from PATH, with `PYTHONPATH` = repo-root `gallery-dl\` (vendored source) + `%LOCALAPPDATA%\GGdown\engine\site-packages`. PySocks must be pip-installed for SOCKS proxies.
- **RELEASE/installed**: `scripts/build-engine.ps1` assembles a Python embeddable (3.13.7) + pinned `gallery-dl==1.32.10` + PySocks into `dist\engine`; the Inno installer puts it in `{app}\engine`, and on first launch `ApplyRuntimeSeeding` copies it to the appdata engine dir (idempotent).

The repo-root `gallery-dl\` checkout (untracked, absent after fresh clone) is expected for the DEBUG path and E2E smoke tests — clone mikf/gallery-dl at the pinned version if those fail/skip.

## Conventions

- Engine protocol changes must keep `PROTOCOL` in `engine/sites/__init__.py` and the C# check in `RunnerEngine.HelloAsync` in sync (both currently v1).
- gallery-dl version is pinned (1.32.10) across `scripts/build-engine.ps1`, `scripts/build.ps1`, and THIRD-PARTY.md — bump all together.
- Error classification in `sites/__init__.py:classify_error` is deliberately conservative (only AuthenticationError / "Could not authenticate you" map to `kind=auth`); don't broaden it, or valid accounts get marked Invalid.
- Keep engine stdout strictly JSONL — all diagnostics go to stderr.
