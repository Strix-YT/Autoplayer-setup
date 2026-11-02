# ApControl: notes for Claude

Battlefield Autoplayer control tool: an ASP.NET Core (.NET 8) server plus a single-page dashboard that drives a fleet of Autoplayer devkits (Xbox / PS5 / PC). Goals and stages: `docs/PROJECT_BRIEF.md`. In-game agent protocol: `docs/GAME_PROTOCOL.md`.

## Commands

```bash
dotnet build ApControl.sln
dotnet test ApControl.sln                        # xUnit, runs against the simulated fleet
dotnet run --project src/ApControl.Server        # http://localhost:5080, 32 simulated APs
```

Run the server from the repo root with `dotnet run --project …`. The content root must be the project folder, or `appsettings.json` and `wwwroot/` won't be found.

## Layout

- `src/ApControl.Core`: pure .NET, no package deps. Registry, controller (bulk commands that wait on reported state), workflow runner (drop-out + retry-failed), status monitor, event bus, and the seams `IPlatformDriver` / `IGameChannel` / `IBuildProvider`.
- `src/ApControl.Drivers`: CLI platform driver (devkit tools, command templates in config), TCP JSON game channel, build providers (Static / Folder / BuildBuddy), `SimulatedFleet`.
- `src/ApControl.Server`: minimal API under `/api`, SSE at `/api/events`, dashboard at `wwwroot/index.html`.
- `Demo - open me (no install).html`: standalone in-browser demo. Keep it in sync with `wwwroot/index.html` when the UI changes.
- `Start Autoplayer Control.bat` / `HOW TO START.txt`: non-developer launcher. Must stay CRLF (enforced by `.gitattributes`).

## Conventions

- Device-specific CLI syntax lives in `appsettings.json` templates, never in code.
- A command counts as successful only when the client *reports* the target state.
- Machine-specific config (real devkit IPs, AppIds, Build Buddy key) goes in `src/ApControl.Server/appsettings.Local.json`, which is git-ignored. See `appsettings.Local.example.json`.
- Keep the repo Battlefield-feature focused: no internal people, team routing or ticket ownership details in docs or code.
- CI (`.github/workflows/ci.yml`) builds and tests on Windows + Ubuntu and publishes a win-x64 artifact from `main`.
