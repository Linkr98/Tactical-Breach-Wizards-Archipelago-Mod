# How to add a mission character requirement

When you find a mission that can't be completed without a specific wizard (like Siege
Cleric needing Banks to resurrect the Druid Hitman), add a **firm requirement**. The logic
then won't consider that mission reachable until you have that wizard.

## The one place you edit

`tools/generate_ap_data.py`, the `FIRM_REQUIRED` dict:

```python
FIRM_REQUIRED = {
    "Game_Liboli_Intro": ["NavySeer"],
    "Game_Necro_Intro":  ["NecroMedic", "WitchCop"],
    "Game_Train":        ["WitchCop"],
    # add your line here:  "<stageID>": ["<InternalChar>", ...],
}
```

- **Key** = the mission's `stageID` (e.g. `Game_Fort_Osprey`).
- **Value** = a list of **internal** character names that mission needs.

### Internal character names (NOT the display names)

| Internal (use this) | In-game name |
|---|---|
| `NavySeer`   | Zan |
| `WitchCop`   | Jen |
| `NecroMedic` | Banks |
| `RiotPriest` | Dall |
| `Druid`      | Rion |

### Things that happen automatically (you don't account for them)

- **Recruits are NOT exempt.** If a mission needs a wizard, list them — even the wizard
  that mission would normally recruit. Controlling a character always requires having
  unlocked them first; there is no "you get them by playing it" exception. (There used to
  be an automatic recruit-subtraction; it was removed on purpose.)
- **Anxiety dreams already require their wizard** — no need to add those.
- **The Asset / Rushwater Reunion are scripted but NOT exempt** — requirements you list
  for them are honored like any other mission (their character needs currently live in
  `HALF_REQUIRED`).

## Finding a mission's stageID

Open `tools/ap_data.json` and search for the mission's display name; the nearby
`"stageID"` is what you want. Or list them all:

```sh
python -c "import json;[print(m['stageID'],'=',m['name']) for m in json.load(open('tools/ap_data.json'))['missions']]"
```

## Apply it (3 steps, from the mod folder)

```sh
# 1. regenerate the shared data from the edited generator
#    (this ALSO auto-copies ap_data.json into the apworld source in custom_worlds)
cd tools && python generate_ap_data.py && cd ..

# 2. re-run the apworld tests
python "/c/ProgramData/Archipelago/custom_worlds/_tactical_breach_wizards_apworld/test_world.py"

# 3. rebuild + install the apworld (writes custom_worlds/tactical_breach_wizards.apworld)
python "/c/ProgramData/Archipelago/custom_worlds/_tactical_breach_wizards_apworld/build_apworld.py"
```

Then generate a new multiworld — already-generated seeds keep their old logic. If other
people play this world too, send them the rebuilt `.apworld` (see
`_tactical_breach_wizards_apworld/README.md`).

`generate_ap_data.py` also rewrites `ApData.cs` for the mod, but a requirement change
doesn't alter any ids/names, so the mod doesn't need rebuilding for this.

## Verify it took

```sh
cd "/c/ProgramData/Archipelago/custom_worlds/_tactical_breach_wizards_apworld" && python -c "
import importlib,sys,types; from pathlib import Path
P='tactical_breach_wizards'; sys.path.insert(0,'.')
m=types.ModuleType(P); m.__path__=[str(Path(P))]; m.__package__=P; sys.modules[P]=m
d=importlib.import_module(P+'.data')
sid='Game_Siege_Cleric'   # <- change to your mission
print(sid,'requires', d.effective_required_characters(d.MISSION_BY_STAGE[sid]))
"
```

`python test_world.py` will also fail loudly if a requirement ever made the seed
unwinnable (its max-reachability check confirms every location is still reachable with all
items, and AP's own generator guarantees solvability per seed).

## Notes / gotchas

- Only use this for things genuinely **required to finish** a mission. Wizards that are
  merely *helpful* should stay unlisted — over-requiring backloads the seed and can make
  generation harder.
- Don't hand-edit `ap_data.json` / `ApData.cs` / `ap_data.py` — they're generated. Always
  edit `generate_ap_data.py` and regenerate, or your change gets overwritten.
- This is for **character** requirements. Mission *ordering* (act gates) lives in
  `C:\ProgramData\Archipelago\custom_worlds\_tactical_breach_wizards_apworld\tactical_breach_wizards\data.py`
  (`ACT_CHARACTER_GATE` / `ACT_GATE_ENABLED`) if you ever want to tune that — rebuild
  with `build_apworld.py` after.

---

# How to add ability / extra-hero requirements (abilities & confidence goals)

Some missions can't be **completed** without a specific ability (e.g. The Blacksite needs
Zan's *False Prophet* to bait a turret), and some **confidence goals** need a particular
ability and/or a second wizard. Add those in `tools/generate_ap_data.py` too.

## Ability savenames (use these strings)

| savename | ability | wizard |
|---|---|---|
| `seerOverwatch`        | Predictive Bolt | Zan |
| `UnlockTimeBoost`      | Time Boost      | Zan |
| `UnlockFalseProphet`   | False Prophet   | Zan |
| `UnlockBroomBreach`    | Broom Breach    | Jen |
| `UnlockGaleGrenade`    | Gale Grenade    | Jen |
| `UnlockChainShock`     | Chain Bolt      | Jen |
| `UnlockGhostShot`      | Spectral Skull  | Banks |
| `UnlockTransference`   | Transference    | Banks |
| `UnlockCrowdGrenade`   | Spore Grenade   | Rion |
| `Unlock_ChainShockSuperchain` | ChainShock Superchain (Jen dream) | Jen |
| `Unlock_DeathsFloor`   | Death's Floor (Banks dream) | Banks |
| `Unlock_SwapWithoutLOS`| Swap w/o LOS (Dall dream) | Dall |
| `Unlock_SporeIntelligent` | Smart Spores (Rion dream) | Rion |

(The plain `Unlock…` names without the underscore are the base-kit abilities; the
`Unlock_…` ones with the underscore are the four dream specials. Death's Floor is the
dream item `Unlock_DeathsFloor` — the game's duplicate base-kit grant `UnlockDeathsFloor`
was RETIRED from the pool 2026-07-19 and the generator will refuse a requirement naming it.)

## 1. Ability needed to COMPLETE a chapter

`FIRM_REQUIRED_ABILITIES` (keyed by mission `stageID`):

```python
FIRM_REQUIRED_ABILITIES = {
    "Game_Liboli_Intro": ["UnlockFalseProphet"],   # Blacksite: bait the turret in room 2
}
```

This gates the mission's **completion check** (you can enter, but can't finish without it).
Heroes-to-complete still go in `FIRM_REQUIRED` (those gate the whole mission).

## 2. Ability / extra hero needed for a specific CONFIDENCE GOAL

### Automatic: goals named after an ability

Goals whose **type name contains an ability** are gated **automatically** — no entry needed.
`DefenestrateWithGaleGoal` requires Gale Grenade (and Jen), `PredictiveShotGoal` requires
Predictive Bolt (and Zan), `GhostShotHitGoal` requires Spectral Skull (and Banks), etc. —
every copy of the goal, in every level. The name→ability map is
`GOAL_NAME_ABILITY_TOKENS` in the generator; it covers all base-kit ability items
(PredictiveShot, TimeBoost, FalseProphet, Broom, Gale, ChainShock, GhostShot, Transference,
DeathsFloor, SporeBomb). The generator prints an "Ability-gated confidence goals" summary
each run so you can see what it caught. Goals about innate kit (RabidBite, Swap, Charge,
RiotBlock, Scapegoat, Resurrect…) need no ability item, so they're deliberately not mapped.

### Manual: everything the name doesn't say

`GOAL_REQUIREMENTS`, keyed by `"<level>|<goalName>"` (the level path and goal name — find
them in `tools/ap_data.json`). Pasting the goal's full location key
`goal:<level>|<goalName>|<n>` straight from `ap_data.json` also works (the `goal:` prefix
and ordinal are stripped automatically). Manual entries **merge with** any auto-detected
abilities:

```python
GOAL_REQUIREMENTS = {
    "Streets/2 Curfew.lvl|PriestTotalKnockbackGoal": {
        "abilities":  ["UnlockChainShock"],   # ability savenames
        "characters": ["WitchCop"],           # EXTRA internal heroes (beyond the goal's own)
        "totalAbilities": 6,                  # team-wide ability count (see below)
    },
}
```

- The goal already implicitly needs its own tagged wizard (the `[Name]` in its title); only
  list **additional** heroes here.
- `abilities`, `characters`, and `totalAbilities` are all optional — include whichever applies.

### Team-wide ability count (`totalAbilities`)

For goals that need **many abilities at once** (e.g. "use 9 abilities in one turn"), set
`totalAbilities` to the count. The apworld then requires the player's best possible squad to
field at least that many abilities. Only as many wizards as the goal's room actually fields
can contribute (the location's `squadSize`, counted automatically from the level's
player-wizard prefabs), and each **unlocked** wizard contributes 4 abilities minus every
BASE-KIT ability item of theirs still missing. Dream specials do **not** count — they upgrade
an existing base ability rather than add a unique one. So `16` in a 5-wizard room demands
most of the roster's base kits; `9` in a 3-wizard room demands three wizards with most of
their kits.

`AbilitiesInOneTurnGoal` goals get their count **automatically** from the level file's
`AbilitiesInOneTurnGoalAbilitiesNeeded` (see `TOTAL_ABILITY_GOAL_PARAMS` in the generator) —
a `totalAbilities` entry here only raises it, never lowers. The generator errors if a goal
would need more abilities than its room's wizards can field. While any such gate exists, all
base-kit ability items are classified progression (the gate may need any of them).

## 2b. The finale capstone (all characters to PLAY, all abilities to WIN)

Two layers, deliberately different:

- **All 5 characters** are a real *physical* requirement — the Roof auto-spawns the whole
  squad — so they live in `FIRM_REQUIRED["Game_Finale_Roof"]` like any other mission
  requirement (gates entering/playing the mission and all its checks).
- **All 14 ability items** are required to **WIN** via `GOAL_REQUIRES_ALL_ABILITIES` (and
  `GOAL_REQUIRES_ALL_CHARACTERS`, redundant but harmless) in the generator. These gate the
  apworld's completion condition only — do NOT express the ability capstone by adding all
  14 abilities to the Roof's `FIRM_REQUIRED_ABILITIES`: that poisons the whole Roof region
  for the fill (its locations then can't hold any gated item) and seeds fail to generate.

Related: the apworld's `generate_early` marks two extra valid-opener mission accesses as
`early_items` — without that, a single-room opener gives the fill a one-location sphere 0
and ~15% of generations failed regardless of any capstone. If you ever loosen/tighten the
opening, re-run a batch of real `ArchipelagoGenerate.exe` seeds (close stdin: failures
block on "Press enter"), not just `test_world.py`.

## 3. Requirements for a mission's HALFWAY checkpoint

Every multi-room mission has a **halfway** location (`Halfway: <name>`) that fires partway
through it (after the first `ceil(rooms/2)` rooms). Single-room missions (e.g. The Traffic
Warlock) have none. By default a halfway check only needs you to *reach* the mission (its
mission-access item + act gate + that mission's firm `FIRM_REQUIRED` characters) — it does
**not** inherit the mission's completion abilities or perk gate, which is what makes it an
easy, early check.

**Single-room missions** (The Traffic Warlock, Achievable Dreams, The Pyromancer, Setting
Jodasa Straight, Kennedy Calls, Cornered) have no halfway location, so a `HALF_REQUIRED*`
entry for them has nothing to attach to — put their requirements in `FIRM_REQUIRED` /
`FIRM_REQUIRED_ABILITIES` instead.

To add logic to a halfway check specifically, use the two new dicts in
`tools/generate_ap_data.py` (same shape as `FIRM_REQUIRED` / `FIRM_REQUIRED_ABILITIES`):

```python
HALF_REQUIRED = {
    "Game_Fort_Osprey": ["NecroMedic"],      # need Banks to reach Fort Osprey's midpoint
}
HALF_REQUIRED_ABILITIES = {
    "Game_Fort_Osprey": ["UnlockGhostShot"], # ...and Ghost Shot
}
```

- **Key** = the mission's `stageID`. **Value** = internal char names / ability savenames
  (same tables as above).
- **The full completion automatically inherits everything you add here.** A mission can only
  be finished by first getting halfway, so anything required for the half is also required
  for the whole — *on top of* that mission's own `FIRM_REQUIRED` / `FIRM_REQUIRED_ABILITIES`
  and perk gate. You never re-list a half requirement on the full check; it's folded in.
- Leave a mission out of both dicts to keep its halfway check requirement-free (just reach
  the mission) — this is the point of the feature: lots of easy early checks.

## 4. OR gates: obstacles with several sufficient solutions

When an obstacle can be solved by **any one of several** wizard/ability combos (instead of
needing everything at once), use `FIRM_REQUIRED_ANY` / `HALF_REQUIRED_ANY` in
`tools/generate_ap_data.py`. Each entry is a list of **alternatives**; an alternative is a
list mixing internal character names and ability savenames (same tables as above), **all**
of which that alternative needs. The check is satisfied as soon as **one whole alternative**
is held.

```python
FIRM_REQUIRED_ANY = {
    # The Recording: cross the gap to finish -- Dall alone, OR Banks + Death's Floor,
    # OR Jen + Broom Breach.
    "Game_The_Recording": [
        ["RiotPriest"],
        ["NecroMedic", "Unlock_DeathsFloor"],
        ["WitchCop", "UnlockBroomBreach"],
    ],
}
```

- `FIRM_REQUIRED_ANY` gates the mission's **completion** check (like
  `FIRM_REQUIRED_ABILITIES`: you can still launch/enter the mission).
- `HALF_REQUIRED_ANY` gates the **halfway** checkpoint, and — like every half
  requirement — is automatically folded into the full completion too. Same single-room
  caveat as the other `HALF_*` knobs: single-room missions have no halfway check, so use
  the FIRM variant there.
- Rare case — a mission with **two independent OR-obstacles**: wrap each in its own list
  (`"Game_X": [[altA1, altA2], [altB1, altB2]]`). Every group must then have one
  alternative satisfied. A plain list of alternatives, as above, is treated as one group.
- Typos fail loudly at generation: a misspelled character name is treated as an ability
  savename and rejected against the real item list.
- The random starting mission logic knows about these gates: a mission only stays in the
  opener pool for a given start hero if every group has an ability-free alternative that
  hero alone satisfies (e.g. The Recording remains a possible opener only for a Dall start,
  if it qualified otherwise).

Apply with the same 3 steps as above (regenerate → test → rebuild the apworld); the mod
doesn't need rebuilding.

## Notes

- **Don't require Time Boost for "multiple actions / abilities in one turn" goals**
  (`SingleWizardActionsInOneTurnGoal`, `AbilitiesInOneTurnGoal`): the base game counts two
  copies of the *same* wizard both acting, so repetition satisfies them — no Time Boost
  needed in logic.
- Use the same **apply** steps as above (regenerate → test; the regenerate step
  auto-copies `ap_data.json` into the live apworld). `test_world.py` re-checks
  reachability after every change.
- Quick way to list every goal's level|name for reference:
  `python -c "import json;[print(l['key'][5:]) for l in json.load(open('tools/ap_data.json'))['locations'] if l['category']=='confidence_goal']"`
