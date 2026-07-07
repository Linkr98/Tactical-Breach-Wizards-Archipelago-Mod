# Tactical Breach Wizards — Archipelago Mod

An [Archipelago](https://archipelago.gg) multiworld randomizer integration for Tactical
Breach Wizards. It has **two halves**:

1. **The C# mod (this folder)** — a BepInEx + Harmony plugin that runs inside the game.
   It talks to the Archipelago server, makes AP the source of truth for unlocks
   (wizards, missions, abilities, perk points, outfits, confidence), and sends
   "location checks" when you complete things.
2. **The apworld (Python)** — the generation/server side, which tells Archipelago what
   items and locations exist and what the logic rules are. It lives in your Archipelago
   install: source + dev tools in
   `C:\ProgramData\Archipelago\custom_worlds\_tactical_breach_wizards_apworld\`, installed as
   `custom_worlds\tactical_breach_wizards.apworld` (built from that source). Start with
   `C:\ProgramData\Archipelago\custom_worlds\_tactical_breach_wizards_apworld\README.md`.

The two halves never share code — they agree on a **frozen numeric id contract**
(82 item ids / 238 location ids). That contract is generated in one place
(`tools/generate_ap_data.py`) and copied to both sides, which is why you must never
hand-edit the generated files (more below).

---

## What the randomizer does (player view)

- **Items you receive from the multiworld:** the 5 wizards, access to each of the 29
  campaign missions, 14 abilities (10 base-kit + 4 anxiety-dream specials), perk points
  (per wizard), per-wizard Confidence Boosts (the *only* way to earn confidence —
  natural earning is blocked), and Donut filler (does nothing). Outfits are NOT items:
  buying one at the shop is the check, and the purchase itself gives you the cosmetic.
- **Checks you send:** completing a mission (29), reaching a mission's halfway room (23),
  in-mission confidence goals (154), buying outfits (23), recruiting a wizard (5), and
  the 4 dream ability-unlock rewards. **238 total.**
- **Start:** one random wizard (with their base kit and 1 perk point) + a random early
  mission. **Goal:** complete "Counterheist: The Roof".
- The game's story progression is otherwise suppressed: every session is "new game"-like
  and the AP server's item list is re-applied on connect, so there is no local save to
  corrupt.

## Playing

1. Make sure the mod is built (see below) — BepInEx loads the DLL from `bin\Debug\`
   automatically when the game starts.
2. Generate a multiworld (yaml options are documented in
   `custom_worlds\_tactical_breach_wizards_apworld\tactical_breach_wizards\docs\setup_en.md`) and host it.
3. Start the game. When you click Play / start / continue a save, the **connect dialog**
   opens — enter host, port, slot name, password. Last-used values are remembered
   (`BepInEx\config\com.lincoln.tbwap.cfg`).
4. In-game keys:
   | Key | What it does |
   |---|---|
   | **F9** | Open/close the connect panel manually |
   | **F10** | Mission hub: launch any unlocked mission, see recent AP activity |
   | **F8** | Re-dump game data to JSON (dev tool, see DataDumper below) |

   Unlocked missions also appear in the game's own mission-replay screen.

---

## Building the C# mod

- From this folder: `dotnet build "Tactical Breach Wizards Archipelago Mod.csproj"` —
  or open `Tactical Breach Wizards Archipelago Mod.slnx` in Visual Studio 2022 and build
  (Debug).
- It's a classic .NET Framework 4.7.2 project. All game/BepInEx references point at the
  game's own DLLs by relative path, so it only builds from inside this folder
  (`...\BepInEx\plugins\...`), which is exactly where it lives.
- Output goes to `bin\Debug\` — BepInEx scans the plugins folder recursively, so **that
  IS the install**; there is no copy step. `ap_icon.png` and
  `Archipelago.MultiClient.Net.dll` are copied next to the DLL automatically.
- **Stick to the Debug configuration.** Building Release too would leave a second DLL in
  `bin\Release\` and BepInEx would load the plugin twice.
- Bump `PluginVersion` in `Class1.cs` when you release a change.
- Logs: `BepInEx\LogOutput.log` (and the BepInEx console if enabled).

## File-by-file guide (C# mod)

Every file has a doc comment at the top explaining its mechanism; this is the map.
Roughly in dependency order:

| File | Role |
|---|---|
| `Class1.cs` | Plugin entry point (`MainMod`): BepInEx config, applies all Harmony patches, spawns `ApManager`. |
| `ApData.cs` | **AUTO-GENERATED** frozen id contract (items, locations, missions). Never edit by hand — regenerate via `tools/generate_ap_data.py`. |
| `ApLookup.cs` | Indexes `ApData` so patches can translate AP ids ↔ game identifiers (stageIDs, character names, outfit saveNames…). |
| `ApState.cs` | The AP-authoritative unlock state (what the server has granted this slot). Patches read this instead of the game's own save. |
| `ApClient.cs` | The network client (wraps Archipelago.MultiClient.Net). Runs on the socket thread; hands items/deaths/notices to the main thread via queues. |
| `ApManager.cs` | The heart of the mod: main-thread MonoBehaviour that drains those queues, applies items, re-syncs game state, sends checks, draws the F10 hub, polls keybinds. |
| `ApConnectUi.cs` | The connect dialog (host/port/slot/password), shown on save start/continue or F9. |
| `ApIcon.cs` | Loads `ap_icon.png` as a sprite + TextMeshPro sprite tag for AP-flavored in-game icons. |
| `CharacterPatches.cs` | Wizard is unlocked ⇔ AP granted the character item. |
| `StagePatches.cs` | Sends mission-complete checks from the campaign flow; fires the goal on the finale. |
| `AnalyticsPatches.cs` | Same, for missions launched as replays/flashbacks (different code path in the game). |
| `MissionSelectPatches.cs` | Shows AP-unlocked (even never-played) missions in the game's replay screen so they can be launched. |
| `ConfidencePatches.cs` | Blocks natural confidence earning (AP Confidence Boosts are the only source). |
| `PerkPatches.cs` | Blocks natural perk-point earning (AP perk_point items are the only source). |
| `SaveSyncPatches.cs` | Re-applies AP grants after every save-data (re)load, which the game does constantly. |
| `DeathLinkPatches.cs` | DeathLink: a custom lethal status condition applied when another player dies; sends a death when your wizard dies. |
| `DataDumper.cs` | Read-only dev tool: dumps the game's progression data to JSON (`tools/tbw_ap_dump.json` input). Runs once at the main menu and on F8. |
| `tools/` | The **generator** (see next section) plus its input dump and generated outputs. |
| `lib/` | `Archipelago.MultiClient.Net.dll`, the AP client library (shipped next to the mod DLL). |
| `ap_icon.png` | The Archipelago logo, loaded at runtime. |

## The id contract & the generator (`tools/`)

`tools/generate_ap_data.py` is the **single source of truth** for every item/location
id, name, and logic requirement. It reads `tools/tbw_ap_dump.json` (a dump the mod wrote
from live game data, F8/DataDumper) plus the level files, and writes:

- `ApData.cs` → compiled into the mod,
- `tools/ap_data.json` + `tools/ap_data.py` → reference copies,
- and **auto-copies `ap_data.json` into the apworld package** in `custom_worlds`.

Rules that keep multiworlds from breaking:

- **Never hand-edit the generated files** (`ApData.cs`, `ap_data.json`, `ap_data.py`) —
  they get overwritten on the next generation, and the two halves would drift apart.
- **Never renumber or remove existing ids.** Generated seeds embed these ids; the
  generator is written to be append-only. Add new stuff at the end.
- After regenerating: run the apworld tests and reinstall the apworld
  (`python C:\ProgramData\Archipelago\custom_worlds\_tactical_breach_wizards_apworld\test_world.py`,
  then `python ...\_tactical_breach_wizards_apworld\build_apworld.py`), and rebuild the mod only if
  `ApData.cs` actually changed. Already-generated seeds keep using the data they were
  generated with — regenerate the multiworld after apworld changes.

**The most common edit you'll make** — marking that a mission/goal needs a specific
wizard or ability — is a one-line change in this generator. Full walkthrough:
[HOWTO-add-mission-requirements.md](HOWTO-add-mission-requirements.md).

## The apworld (Python side)

Lives at `C:\ProgramData\Archipelago\custom_worlds\`:

- `tactical_breach_wizards.apworld` — the **installed** world, the thing Archipelago
  actually loads. It's just a zip of the source below; never edit it directly.
- `_tactical_breach_wizards_apworld\` — the **source** and dev tools. The underscore prefix makes
  Archipelago ignore this folder (your Archipelago build can only load zipped
  `.apworld` files, not unpacked folders — we verified). Contains:
  - `tactical_breach_wizards\` — the world source you edit (`rules.py`, `data.py`,
    `options.py`, `__init__.py`, plus the generated `ap_data.json`),
  - `test_world.py` — fast sanity/logic tests, no AP install needed,
  - `build_apworld.py` — zips the source and **installs it** (writes the `.apworld`
    into `custom_worlds\`). Run this after every source edit; give the same file to
    other players,
  - `README.md` — the full logic model, pool math, and workflows.

So the edit loop is: edit source → `python test_world.py` → `python build_apworld.py`
→ generate.

## Other docs

- [HOWTO-add-mission-requirements.md](HOWTO-add-mission-requirements.md) — add
  character/ability requirements to missions, goals, and halfway checkpoints.
- `C:\ProgramData\Archipelago\custom_worlds\_tactical_breach_wizards_apworld\README.md` — apworld
  internals: item pool math, logic/gating model, options, testing, building.
- [APWORLD_HANDOFF.md](APWORLD_HANDOFF.md) — the original design spec the apworld was
  built from. **Historical**; its counts predate the halfway checkpoints. Kept because
  it explains the *why* behind the logic decisions.
