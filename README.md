# Autoplayer Control (ApControl)

[![CI](https://github.com/Strix-YT/Autoplayer-setup/actions/workflows/ci.yml/badge.svg)](https://github.com/Strix-YT/Autoplayer-setup/actions/workflows/ci.yml)

One control tool for Autoplayers: build install, game boot, matchmaking, deploy and autoplay for many devkits at once, all from one dashboard.
This is **Stage 1, Centralized Autoplayer Control**, from the opportunity brief ([docs/PROJECT_BRIEF.md](docs/PROJECT_BRIEF.md)), with Stage 2 hooks already in place (groups, workflows, exception summary with retry, build-provider seam for Build Buddy).

> Success criterion (Stage 1): one QD manages 20+ Autoplayers with no hands on the individual consoles.

## Run it (no hardware needed)

Requires the .NET 8 SDK.

```bash
dotnet run --project src/ApControl.Server        # simulated fleet of 32 APs
# open http://localhost:5080
dotnet test                                      # engine tests against the simulator
```

Opening `src/ApControl.Server/wwwroot/index.html` directly in a browser also works. With no server behind it, the page runs an in-browser demo fleet.

### Typical flow (from the brief)

1. **Select All** → workflow **Prepare Conquest** (Launch → Join Conquest → Deploy → Start AP)
2. The run panel shows **30/32 Ready** and lists the failures (`AP-017 — install failed`, …) → **Retry failed clients**
3. **Select All** → **Swap to Breakthrough**
4. End of session → **End of playtest** (Stop), so no console is left idling in FE and generating bad MTBF data

Selection works with click, ctrl-click, shift-click ranges, *All / None / Invert / Errors*, the state chips in the header (e.g. click "Error 2"), and named groups. *Save selection as group* creates groups such as "Attackers".

## Architecture

```
┌────────────── ApControl.Server (ASP.NET Core, minimal API) ───────────────┐
│  wwwroot/index.html  ← dashboard (SSE live updates)                         │
│  /api/clients /api/commands /api/workflows/* /api/groups /api/builds        │
│  /api/events (Server-Sent Events)                                           │
└──────────────┬─────────────────────────────────────────────────────────────┘
               │
┌──────────────▼──────────── ApControl.Core (pure .NET, no deps) ────────────┐
│  ClientRegistry   fleet inventory + live state + groups                     │
│  AutoplayerController  bulk commands, parallel, waits on real state         │
│  WorkflowRunner   step sequences, drop-out on failure, retry-from-step      │
│  StatusMonitor    heartbeat: ping + in-game status, crash detection         │
│  EventBus         everything the dashboard sees                             │
│  IPlatformDriver | IGameChannel | IBuildProvider   ← seams                  │
└──────────────┬──────────────────────┬───────────────────────┬──────────────┘
   CliPlatformDriver           TcpJsonGameChannel        Static / Folder /
   (xbapp, xbreboot,           (in-game agent,           BuildBuddyBuildProvider
    prospero-ctrl …)            docs/GAME_PROTOCOL.md)
               └──── SimulatedFleet implements both for dev + tests ────┘
```

### Why this split

- **Device vs game layer.** Vendor tools already handle install, launch and reboot. Matchmaking, deploy and autoplay need a small agent inside the game. Keeping the two separate means Stage 1 device control can ship before the game-side agent lands.
- **CLI commands live in config.** Xbox and PS5 tool syntax changes between SDK versions, so you can fix a command in `appsettings.json` without a rebuild.
- **Every command waits on reported state.** "Join" counts as done only when the game reports `InGame`, not when the command has been sent. This is what makes the exception summary trustworthy.

## Configuration (`src/ApControl.Server/appsettings.json`)

Keep machine-specific settings (real devkit inventory, AppIds, Build Buddy URL/key) in `src/ApControl.Server/appsettings.Local.json`. Git ignores that file, and it overrides `appsettings.json`. Start by copying `appsettings.Local.example.json`.

| Key | What |
|---|---|
| `Mode` | `Simulated` or `Live` |
| `Clients` | Inventory: `Id`, `Name`, `Platform` (Xbox/PS5/PC), `Address`, `Groups`. If empty in Simulated mode, 32 clients are generated |
| `Experiences`, `Profiles`, `Workflows` | Dropdowns and one-click sequences |
| `Platforms.{Xbox,PS5}.Cli` | Ping / Install / Launch / Terminate / Reboot command templates. Placeholders: `{address} {appId} {args} {packagePath} {buildVersion} {id} {name}` |
| `Builds.Provider` | `Static`, `Folder` (scan a build share) or `BuildBuddy` |
| `Control` | Parallelism and timeouts |

⚠️ **The Xbox and PS5 CLI templates are starting points. Check them against your GDK and Prospero SDK versions before going Live.** Also fill in the real `AppId` (AUMID / title id).

## Build Buddy integration

All build lookups go through `IBuildProvider`. `BuildBuddyBuildProvider` currently assumes a REST endpoint (`GET /api/builds?platform=&branch=`). Once we know Build Buddy's real interface (REST, CLI, or a shared .NET library), only that one class changes. Next steps:

- The build picker lists Build Buddy builds
- Install validation: after install, compare the version the game reports with the build you picked
- Stage 2 "Prepare Group A": Build Buddy fetch → install → boot → join → deploy → AP

## Roadmap mapping

| Stage | Status here |
|---|---|
| 1 — Centralized control | ✅ dashboard, select/groups/all, state, launch w/ args, start/stop, join/leave/switch, deploy, restart, bulk, error feedback, live observability |
| 2 — Automated setup & groups | 🟡 workflows, groups, exception summary + retry, install via build provider. **Todo:** persisted groups/configs, auto-recover policy, log/crash-dump/video collection (Nexus?) |
| 3 — Scenario control | ⬜ `ap.order` commands reserved in protocol; named groups already work as targets |
| 4 — Orchestration | ⬜ conditional steps ("wait until 30+ deployed"), loadouts, reports |

## Open questions / risks

| Risk | Impact | Mitigation |
|---|---|---|
| No in-game control agent yet | High | Protocol drafted (`docs/GAME_PROTOCOL.md`); device-only control works without it |
| Devkit CLI syntax / permissions on the host machine | Med | Templates in config; run the server on a machine with GDK + Target Manager installed |
| Network access from the tool host to all devkits (ports 4711 + vendor ports) | Med | Validate with IT on the playtest network early |
| Build Buddy interface unknown | Low | Isolated behind `IBuildProvider` |
