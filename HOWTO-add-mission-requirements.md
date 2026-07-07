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
| `UnlockDeathsFloor`    | Death's Floor   | Banks |
| `UnlockCrowdGrenade`   | Spore Grenade   | Rion |
| `Unlock_ChainShockSuperchain` | ChainShock Superchain (Jen dream) | Jen |
| `Unlock_DeathsFloor`   | Death's Floor superchain (Banks dream) | Banks |
| `Unlock_SwapWithoutLOS`| Swap w/o LOS (Dall dream) | Dall |
| `Unlock_SporeIntelligent` | Smart Spores (Rion dream) | Rion |

(The plain `Unlock…` names without the underscore are the base-kit abilities; the
`Unlock_…` ones with the underscore are the four dream "superchain" specials.)

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

`GOAL_REQUIREMENTS`, keyed by `"<level>|<goalName>"` (the level path and goal name — find
them in `tools/ap_data.json`, or in the goal's location key `goal:<level>|<goalName>|<n>`):

```python
GOAL_REQUIREMENTS = {
    "Streets/2 Curfew.lvl|PriestTotalKnockbackGoal": {
        "abilities":  ["UnlockChainShock"],   # ability savenames
        "characters": ["WitchCop"],           # EXTRA internal heroes (beyond the goal's own)
    },
}
```

- The goal already implicitly needs its own tagged wizard (the `[Name]` in its title); only
  list **additional** heroes here.
- `abilities` and `characters` are both optional — include whichever applies.

## 3. Requirements for a mission's HALFWAY checkpoint

Every multi-room mission has a **halfway** location (`Halfway: <name>`) that fires partway
through it (after the first `ceil(rooms/2)` rooms). Single-room missions (e.g. The Traffic
Warlock) have none. By default a halfway check only needs you to *reach* the mission (its
mission-access item + act gate + that mission's firm `FIRM_REQUIRED` characters) — it does
**not** inherit the mission's completion abilities or perk gate, which is what makes it an
easy, early check.

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
