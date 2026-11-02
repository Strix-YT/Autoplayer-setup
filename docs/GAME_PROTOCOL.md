# In-game Autoplayer agent protocol (v0 draft)

The control tool drives two layers:

| Layer | Who implements | How |
|---|---|---|
| **Device**: install, launch, kill, reboot, ping | Already exists | Vendor CLIs (Xbox GDK `xb*` tools, PS5 `prospero-ctrl`), wrapped by `CliPlatformDriver` |
| **Game**: join, leave, deploy, start/stop autoplayer, status | **Game / AP team** | A small TCP listener inside the game client, described here |

The game layer is the main dependency for Stage 1. Without it, the tool can boot and kill builds but can't matchmake, deploy, or see in-game state.

## Transport

- The game client listens on TCP port `4711` (configurable: `ApControl:Game:Port`, or per-client `GamePort`). Only enable this in AP / dev builds, e.g. behind the `-autoplayer` launch argument.
- Each connection carries one request and one reply. Both are single-line UTF-8 JSON terminated by `\n`.
- The game must reply within 5 s (`ApControl:Game:TimeoutSeconds`). Long operations (matchmaking) return `ok` right away and report progress through `status`.

## Request

```json
{"cmd":"join","args":{"experience":"conquest"}}
```

## Reply

```json
{"ok":true,"state":"Matchmaking","experience":"conquest","build":"CL1234567","message":null}
```

`state` is required for `status` and optional otherwise. On failure send `ok:false` with a human-readable `message`; it is shown on the dashboard tile.

## Commands

| cmd | args | Expected effect / state progression |
|---|---|---|
| `status` | – | Report the current state. Called every 2–5 s |
| `join` | `experience` + any `JoinArgs` from config (playlist, server…) | `InFrontend → Matchmaking → InGame` |
| `leave` | – | `→ InFrontend` |
| `deploy` | `team` (optional) | `InGame → Deployed` |
| `ap.start` | `profile` (Standard / Aggressive / Passive / Objective…) | `Deployed → Autoplaying` |
| `ap.stop` | – | `Autoplaying → Deployed` |

## State values

`Booting`, `InFrontend`, `Matchmaking`, `InGame`, `Deployed`, `Autoplaying`, `Error`

Report `Error` with a `message` for in-game failures such as a server kick or a failed join. Treat a crash or a closed listener as "not answering": the tool flags it as *"Game stopped responding — possible crash or disconnect"*.

## Reserved for later stages

- Stage 3: `ap.order` with `{"order":"capture","target":"A"}`, `defend`, `gather` `{x,y,z}`, `push` `{sector}`, `resume`
- Stage 4: `loadout.set` `{primary, secondary, gadgets…}` and `loadout.report`, which returns the items actually used
