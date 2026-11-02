# Project brief: Autoplayer Control Tool

Summary of the opportunity brief. The README's *Roadmap mapping* shows where the code stands against each stage.

**Target:** September FY27

## Vision

One control tool for Autoplayers (APs), covering:

- build install and game boot
- matchmaking (join / quit / switch)
- AP profiles (aggressive, passive, …)
- coordinated actions (capture a point, take a sector, defend, gather at a location)
- loadout control (random or specified) and loadout-usage reporting (weapons / gadgets / cosmetics used)

## Today's process (the pain)

1. Each participant, or a few people covering all consoles, installs the build on their AP console.
2. The game is booted by hand through Xbox Manager / PS5 Target Manager with that session's command line.
3. Each person drives their own AP remotely or with a controller at the desk, and joins or swaps modes as the coordinator announces them.
4. On a disconnect, crash or AP bug, each person has to notice it on their second monitor and collect logs, video and crash dumps by hand for the coordinator.
5. At the end, everyone has to close their AP so none sit in gameplay or FE. Idle APs generate invalid MTBF data.

## Stages

### Stage 1: Centralized control *(critical milestone)*

**Goal:** one Playtest Coordinator (QD) controls many APs.

- Dashboard of all AP clients; select one, many, a group, or all
- State per client: Online / Offline / In Game / Error, with real-time observability (no inspecting the device)
- Remote launch with arguments; start/stop AP; join / leave / switch experience; deploy; restart
- Bulk commands and basic error/status feedback

*Example:* Select 32 → Launch → Join Conquest → Deploy → Start AP. Then: Select All → Leave → Join Breakthrough → Deploy.

**Success:** a playtest that needed 20 testers + 20 AP devices runs with **one QD managing 20 APs**.

### Stage 2: Automated setup & group management

**Goal:** remove the QD's remaining repetitive setup work.

- Build management, remote install/update, build/version validation
- Launch-argument configs, auto-recovery/reconnect, groups, reusable configurations
- One-click "Prepare Group A" (e.g. 32 clients, latest QV build, Conquest, 16/16 teams, standard profile): install → boot → start → matchmake → deploy → AP on
- An exception-first summary, e.g. `30/32 Ready, AP-017 install failed, AP-024 failed to join`, with **Retry failed clients**
- Open: automatic video capture and log/crash-dump collection to a set location (Nexus?), and possibly a live video wall of the AP group

**Success:** one QD prepares a whole AP group, including finding and recovering failed clients, without touching any machine.

### Stage 3: Gameplay & scenario control

**Goal:** APs can stand in for human players in scenarios.

- Named groups (Attackers 16 / Defenders 12 / Passive 4)
- Orders per group: capture A, defend B, gather at X, push sector, resume autonomous play
- AP profiles (TBD)

**Success:** one QD coordinates AP groups to reproduce multiplayer scenarios alongside real testers.

### Stage 4: Test orchestration

**Goal:** repeatable multiplayer test scenarios, not just remote control.

- Saved scenarios, command sequencing, conditional steps ("wait until 30+ deployed")
- Loadout distributions, telemetry, reports, coverage tracking, integration with other QA tests
- Example report: requested vs joined, team split, weapons/gadgets exercised, disconnects, duration
- Scenarios become reusable assets: "Reproduce bug …", "Vehicle stress", "Full-server Breakthrough progression"

**Success:** common multiplayer test scenarios launch and repeat with minimal manual coordination.

## Still open in the brief

Business value, the plan to operationalize, current build-deployment pain points, the timeline/milestones table, and the risk register are all still TBD. See *Open questions / risks* in the README for the technical risks found so far.
