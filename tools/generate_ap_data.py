#!/usr/bin/env python3
"""
Generates the FROZEN Archipelago id contract for Tactical Breach Wizards from the
in-game data dump (tbw_ap_dump.json, produced by the mod's DataDumper).

Outputs (single source of truth for BOTH halves of the integration):
  - ap_data.json   : canonical item/location tables with stable numeric ids
  - ApData.cs      : C# constants for the BepInEx mod (compiled into the plugin)
  - ap_data.py     : Python tables for the .apworld

IDS MUST STAY STABLE once seeds exist. Ordering here is deterministic (campaign
order for missions/goals, fixed character order for outfits/perks). Never renumber
an existing entry; only append.

Run:  python tools/generate_ap_data.py
"""
import json, os, re

BASE = 271828000  # arbitrary stable base offset for this game's id space

# Distinct id ranges (item space and location space are independent in AP).
ITEM = {
    "character":     BASE + 1000,   # 5
    "mission_access":BASE + 1100,   # 29
    "ability":       BASE + 1300,   # 5
    "perk_point":    BASE + 1400,   # one code per playable character
    "outfit":        BASE + 1500,   # 23 (filler cosmetics)
    "confidence":    BASE + 1600,   # one code per character; the only confidence source
    "filler":        BASE + 1900,   # generic junk to pad the pool
}
LOC = {
    "mission_complete": BASE + 2000,  # 29
    "confidence_goal":  BASE + 2100,  # 154
    "outfit_purchase":  BASE + 2400,  # 23
    "ability_unlock":   BASE + 2500,  # 5
    "recruit":          BASE + 2600,  # 5
    "mission_half":     BASE + 2700,  # 23 (halfway checkpoint for multi-room missions)
}

PLAYABLE = ["NavySeer", "WitchCop", "NecroMedic", "RiotPriest", "Druid"]

# Core "base-kit" ability perks the campaign grants via grantPerk[] in its level files. The mod
# used to auto-grant all of these at the start of a run; now they are AP items (unlocked from AP
# instead). Each is a CharacterPerk identified by its savename; the mod acquires it on receipt
# (key "ability:<savename>"). APPENDED after the 5 PerkUnlock specials so existing ability ids
# (ChainShockSuperchain..SeerFinaleKnockBackDummy = +1300..+1304) stay frozen; never reorder.
# (savename, display label, applicable character) -- character is from the live perk metadata.
BASE_ABILITIES = [
    ("seerOverwatch",      "Predictive Bolt", "NavySeer"),
    ("UnlockTimeBoost",    "Time Boost",      "NavySeer"),
    ("UnlockFalseProphet", "False Prophet",   "NavySeer"),
    ("UnlockBroomBreach",  "Broom Breach",    "WitchCop"),
    ("UnlockGaleGrenade",  "Gale Grenade",    "WitchCop"),
    ("UnlockChainShock",   "Chain Bolt",      "WitchCop"),
    ("UnlockGhostShot",    "Spectral Skull",  "NecroMedic"),
    ("UnlockTransference", "Transference",    "NecroMedic"),
    # NOTE: Banks's BASE ability "Death's Door" (portal on a wall) is INNATE -- no grantPerk
    # unlocks it anywhere, so it has no AP item. UnlockDeathsFloor is NOT it: it's the game's
    # redundant catch-up grant (Two Trains/3 Chasers) of the DREAM upgrade "Death's Floor"
    # (portal beneath someone, dumps them out of an existing Death's Door). The dream reward
    # item ability:Unlock_DeathsFloor grants the same ability via a different perk savename
    # ("DeathsFloor") -- vanilla grants it twice. Two AP items for one ability confused
    # players, so this copy is RETIRED (see RETIRED_ABILITY_SAVENAMES below); it stays in
    # this list ONLY so the ids after it never shift. (Verified against sharedassets0
    # perk/ability text and StreamingAssets/PerkProgress snapshots, 2026-07-14.)
    ("UnlockDeathsFloor",  "Death's Floor",   "NecroMedic"),
    ("UnlockCrowdGrenade", "Spore Grenade",   "Druid"),
]

# RETIRED ability items: still DEFINED in the contract (ids are frozen; the mod keeps
# blocking their vanilla grantPerk and can still apply them if an OLD seed sends one), but
# new pools never place them and logic may never reference them (the generator errors if a
# requirement knob names one). UnlockDeathsFloor retired 2026-07-19: the dream item
# ability:Unlock_DeathsFloor is THE Death's Floor item now -- it takes over the friendly
# name and counts as part of Banks's base kit (baseKit flag) for the team-ability count and
# the missing-kit perk penalty, exactly like the retired copy used to.
RETIRED_ABILITY_SAVENAMES = {"UnlockDeathsFloor"}

# Dream (PerkUnlock) specials that stand in for a base-kit ability: stageID -> (item display
# name, owning internal character). These get the baseKit flag and a friendly name instead of
# the raw "Ability: Unlock_X" stage name. Currently only Death's Floor (the other three
# specials are pure upgrades to an existing ability, not abilities of their own).
SPECIAL_ABILITY_BASEKIT = {
    "Unlock_DeathsFloor": ("Ability: Death's Floor (Banks)", "NecroMedic"),
}

# Internal CharacterNames -> in-game display name (used in all user-facing AP names).
NAME_MAP = {
    "NavySeer": "Zan",
    "WitchCop": "Jen",
    "NecroMedic": "Banks",
    "RiotPriest": "Dall",
    "Druid": "Rion",
}
def disp(ch):
    return NAME_MAP.get(ch, ch)

# Victory condition: completing the final mission.
GOAL_STAGE_ID = "Game_Finale_Roof"  # "Counterheist: The Roof" (verified below against dump)
# VICTORY additionally requires the FULL roster and EVERY ability item (all 5 wizards,
# all 4 dream specials + all 10 base-kit abilities) -- a 100%-your-kit capstone (project
# owner's rule, 2026-07-14). Emitted as flags on the json "goal" dict; the apworld applies
# them to the COMPLETION CONDITION only, never to location/region access rules. (Folding
# them into the goal mission's requiredCharacters/requiredAbilities poisons the whole Roof
# region for fill -- those locations then can't hold any of the ~19 gated items -- and made
# ~1 in 6 seeds fail with FillError; victory-condition-only gating leaves fill untouched.)
GOAL_REQUIRES_ALL_CHARACTERS = True
GOAL_REQUIRES_ALL_ABILITIES = True

# Firm per-mission character requirements (user-specified). Everything else is left
# to the apworld's loose logic (see charactersUsed hint). A mission's own recruit is NOT
# exempt: if you control a wizard in a mission, you must have unlocked them first, even
# the one that mission would normally hand you (project owner's rule, 2026-07-07).
FIRM_REQUIRED = {
    
    "Game_Liboli_Intro": ["NavySeer"], #Zan required for last room for story
    "Game_Necro_Intro": ["NecroMedic", "WitchCop"],
    
    "Game_Train": ["WitchCop"], #Objective Jen must reach the Green Zone 
    "Game_Flashback": ["NavySeer"], #Zan story mission
    "Game_Kalan_Ambush_2": ["NecroMedic"], #Banks needs to be pushed into deaths door
    "Game_Lucid_Dream_Zan": ["WitchCop", "NavySeer"],
    "Game_Finale_Vault": ["WitchCop"], #Jen needs to do glyphs
    "Game_Finale_Mines": ["WitchCop", "RiotPriest"], #Survive until both arrive
    # The Roof auto-spawns ALL five wizards, so the full roster is needed just to PLAY it
    # (region gate). The all-ABILITIES capstone stays on the victory condition only
    # (GOAL_REQUIRES_ALL_ABILITIES) -- see the fill-poisoning warning above.
    "Game_Finale_Roof": ["NavySeer", "WitchCop", "NecroMedic", "RiotPriest", "Druid"],
}

# Characters a mission AUTO-GIVES you: scripted squad members you end up CONTROLLING
# whether or not you own them. The MOD blocks LAUNCHING these missions until the listed
# wizards are unlocked (ApData.LaunchRequiredCharacters). Deliberately NARROWER than
# FIRM_/HALF_REQUIRED: missions that merely NEED a wizard/ability to progress (Siege
# Cleric's resurrect, Basic Training's "Jen must reach the Green Zone", Vault's glyphs,
# The Necromedic, Cornered) stay launchable -- you can load them, you just can't finish
# without the requirement, which matches logic (project owner, 2026-07-14).
# An anxiety dream's own wizard is added automatically; list only EXTRA scripted cast.
AUTO_GIVEN_CHARACTERS = {
    "Game_Prologue":          ["NavySeer"],               # tutorial fields Zan
    "Game_Witch_Intro":       ["NavySeer", "WitchCop"],   # scripted tutorial cast
    "Game_Liboli_Intro":      ["NavySeer"],               # story auto-fields Zan (last room)
    "Game_Streets":           ["RiotPriest"],
    "Game_Flashback":         ["NavySeer"],               # Zan solo story flashback
    # Banks's dream: the rest of the squad lies dead in it and is revived/controlled.
    "Game_Lucid_Dream_Banks": ["NavySeer", "WitchCop", "RiotPriest", "Druid"],
    "Game_Lucid_Dream_Zan":   ["WitchCop"],               # Jen appears in Zan's dream
    "Game_Finale_Mines":      ["WitchCop", "RiotPriest"], # Jen and Dall arrive mid-mission
    "Game_Finale_Roof":       ["NavySeer", "WitchCop", "NecroMedic", "RiotPriest", "Druid"],
}

# Override where a character is RECRUITED (i.e. which mission's completion fires their
# "Recruit: <Name>" location check, both here and in the mod's RecruitByMission), when the
# dump's characterUnlocks disagrees with where they actually join in-game. Banks joins at the
# END of The Blacksite; The Necromedic is only her tutorial ("learn to play Banks"), so his
# recruit moves off Necro Intro to Blacksite.
RECRUIT_OVERRIDE = {
    "NecroMedic": "Game_Liboli_Intro",
}

# Abilities required to COMPLETE a chapter/level (mission). Keyed by stageID; values are
# ability savenames (the part after "ability:" in an ability item key -- e.g. base-kit
# "UnlockFalseProphet" or a PerkUnlock special "Unlock_ChainShockSuperchain"). Gate the
# completion check. Heroes-to-complete go in FIRM_REQUIRED above; abilities go here.
# Fill in as you find them (see HOWTO-add-mission-requirements.md). 
FIRM_REQUIRED_ABILITIES = {
    "Game_Witch_Intro": ["seerOverwatch"], #Needed for set trap main objective

}

# ---------------------------------------------------------------------------
# HALFWAY-CHECKPOINT requirements. Each multi-room mission gets a "mission_half" location
# that fires partway through (after the first ceil(N/2) rooms) -- an easier, EARLIER check
# than finishing the mission. These are the knobs to add logic to those halfway checks, exactly
# like FIRM_REQUIRED / FIRM_REQUIRED_ABILITIES gate the full completion:
#   HALF_REQUIRED[stageID]            = extra INTERNAL character names to reach the halfway point
#   HALF_REQUIRED_ABILITIES[stageID]  = ability savenames to reach the halfway point
# Anything you add here is ALSO imposed on the full mission completion (the whole mission can
# only be finished by first getting halfway), on TOP of that mission's existing full requirements
# -- so the full check always assumes every restriction of the half plus its own. Leave a mission
# out to give its halfway check no requirements beyond simply reaching the mission (region access:
# its mission_access item + act gate + firm requiredCharacters). Dream gating from the region
# still applies; these are purely additive on top. (See HOWTO doc.)
HALF_REQUIRED = {
    "Game_Prologue":    ["NavySeer"],            # tutorial requires Zan
    "Game_Witch_Intro": ["WitchCop", "NavySeer"],# Rushwater PD requires Jen and Zan
    "Game_Liboli_Intro": ["NavySeer"], #Need Zan to use False pophit for room 2
    "Game_Necro_Intro": ["WitchCop", "NecroMedic"], #Banks for res Jen, Jen needed for broom breach
    "Game_Streets":     ["RiotPriest"],
    "Game_Siege_Cleric": ["NecroMedic"],  # Siege Cleric L1 has a required ResurrectDruidObjective -> needs Banks's resurrect.
    "Game_Lucid_Dream_Banks": ["NecroMedic", "NavySeer", "WitchCop", "RiotPriest", "Druid"],  # All characters show as dead in the dream and can be revived
    
}
HALF_REQUIRED_ABILITIES = {
    "Game_Liboli_Intro": ["UnlockFalseProphet"],   # Blacksite: bait the turret in room 2
    # (Resurrect has no item -- it's innate to Banks, so the NecroMedic entry in
    # HALF_REQUIRED above already covers "res Jen".)
    "Game_Necro_Intro": ["UnlockBroomBreach"], # Broom breach tutorial room
    "Game_Lucid_Dream_Zan": ["UnlockFalseProphet"],

}

# ---------------------------------------------------------------------------
# OR-gated requirements: obstacles with SEVERAL sufficient solutions. Where FIRM_REQUIRED*
# / HALF_REQUIRED* demand EVERY listed thing, these demand AT LEAST ONE alternative --
# and each alternative is a list mixing internal character names and ability savenames
# (same tables as above), ALL of which that alternative needs.
#   FIRM_REQUIRED_ANY[stageID] = [alt, alt, ...]   -> gates the mission's COMPLETION check
#   HALF_REQUIRED_ANY[stageID] = [alt, alt, ...]   -> gates the HALFWAY checkpoint (and, like
#                                                     all half requirements, folds into the
#                                                     full completion automatically)
# Like FIRM_REQUIRED_ABILITIES this gates progress, not entry: the mission stays launchable,
# you just can't finish (or get halfway) without satisfying one alternative. A mission that
# has SEVERAL independent OR-obstacles can pass a list of groups instead ([[alt, ...],
# [alt, ...]] -- every group must have one alternative satisfied); a plain list of
# alternatives is treated as a single group.
FIRM_REQUIRED_ANY = {
    # The Recording: crossing the gap to finish. Dall's innate swap does it alone; Banks
    # needs Death's Floor; Jen needs Broom Breach.
    "Game_The_Recording": [
        ["RiotPriest"],
        ["NecroMedic", "Unlock_DeathsFloor"],
        ["WitchCop", "UnlockBroomBreach"],
    ],
}
HALF_REQUIRED_ANY = {
}

def _norm_any_requirements(knob_name, knob):
    """Normalize an *_ANY knob to {stageID: [group, ...]} with group = [alternative, ...]
    and alternative = {"characters": [...], "abilities": [...]}. Accepts per mission either
    one group (a list of token-list alternatives) or a list of such groups. Tokens are
    classified by membership in PLAYABLE; anything else is treated as an ability savename
    (validated against the real item pool later in build())."""
    out = {}
    for sid, value in knob.items():
        if value and all(isinstance(a, list) and a and all(isinstance(t, str) for t in a)
                         for a in value):
            groups = [value]          # a single group of alternatives
        else:
            groups = value            # already a list of groups
        norm = []
        for gi, group in enumerate(groups):
            if not isinstance(group, list) or not group:
                raise AssertionError(
                    f"{knob_name}[{sid}] group {gi}: must be a non-empty list of alternatives")
            alts = []
            for alt in group:
                if not isinstance(alt, list) or not alt or not all(isinstance(t, str) for t in alt):
                    raise AssertionError(
                        f"{knob_name}[{sid}] group {gi}: each alternative must be a non-empty "
                        f"list of character/ability names, got {alt!r}")
                alts.append({"characters": sorted({t for t in alt if t in PLAYABLE}),
                             "abilities":  sorted({t for t in alt if t not in PLAYABLE})})
            norm.append(alts)
        out[sid] = norm
    return out

FIRM_REQUIRED_ANY = _norm_any_requirements("FIRM_REQUIRED_ANY", FIRM_REQUIRED_ANY)
HALF_REQUIRED_ANY = _norm_any_requirements("HALF_REQUIRED_ANY", HALF_REQUIRED_ANY)

# ---------------------------------------------------------------------------
# AUTO-DETECTED confidence-goal ability requirements. Many goal type names literally
# contain the ability they need ("DefenestrateWithGaleGoal" -> Gale Grenade,
# "PredictiveShotGoal" -> Predictive Bolt). Any goal whose name contains one of these
# tokens automatically requires that ability item AND its owning wizard -- no
# GOAL_REQUIREMENTS entry needed. Applies to every copy of the goal in every level.
# GOAL_REQUIREMENTS below is still merged on top for anything the name doesn't say.
# Only abilities that exist as AP items belong here (base-kit savenames from
# BASE_ABILITIES); goals about innate kit (RabidBite, Swap, Charge, RiotBlock,
# Scapegoat, Resurrect...) need no item so they are deliberately absent.
GOAL_NAME_ABILITY_TOKENS = {
    # Tokens are CASE-SENSITIVE substrings of goal type names; the generator errors if a
    # token matches no goal, so dead entries can't sit here silently.
    "PredictiveShot": "seerOverwatch",       # PredictiveShotGoal, PredictiveShotFinaleGoal
    "Broom":          "UnlockBroomBreach",   # BroomDefenestrationGoal
    "Gale":           "UnlockGaleGrenade",   # DefenestrateWithGaleGoal
    "ChainShock":     "UnlockChainShock",    # ChainShockFinaleGoal
    "GhostShot":      "UnlockGhostShot",     # GhostShotHitGoal
    # Scapegoat*Goal: the "victim" half of Transference IS the scapegoat (ability text:
    # "the victim takes the hit instead"), so these need the Transference item.
    "Scapegoat":      "UnlockTransference",
    # DeathsFloorDistanceGoal. The dream item is THE Death's Floor item (the base-kit
    # duplicate is retired -- see RETIRED_ABILITY_SAVENAMES).
    "DeathsFloor":    "Unlock_DeathsFloor",
    "SporeBomb":      "UnlockCrowdGrenade",  # SporeBomb*Goal -- "spore bomb" = Spore Grenade item
    # "DeathsDoor" goals are deliberately ABSENT: Banks's base Death's Door is INNATE
    # (no grantPerk anywhere; her tutorial room already goal-tracks it), so there is no
    # item to require -- the goals' own Banks tag (or a characters entry, see
    # DeathsDoorWarlockGoal in GOAL_REQUIREMENTS) is the whole gate.
}
_ABILITY_OWNER = {sv: ch for sv, _label, ch in BASE_ABILITIES}
_ABILITY_OWNER.update({sv: ch for sv, (_label, ch) in SPECIAL_ABILITY_BASEKIT.items()})

def goal_auto_requirements(goal_name):
    """(ability savenames, owning internal characters) implied by the goal type's name."""
    abilities = sorted({sv for tok, sv in GOAL_NAME_ABILITY_TOKENS.items() if tok in goal_name})
    chars = sorted({_ABILITY_OWNER[sv] for sv in abilities if sv in _ABILITY_OWNER})
    return abilities, chars

# Extra requirements for a specific CONFIDENCE GOAL, keyed by "<level>|<goalName>"
# (ordinal-agnostic; applies to all copies of that goal). Pasting the full location key
# ("goal:<level>|<goalName>|<n>") also works -- the "goal:" prefix and ordinal are
# stripped on load. Abilities named in the goal type itself are added automatically
# (GOAL_NAME_ABILITY_TOKENS above); only list what the name doesn't already say.
# Each goal already implicitly needs
# its own tagged wizard; list ADDITIONAL heroes (internal names), ability savenames, and/or
# a TEAM-WIDE ability count ("totalAbilities"). The team count is gated in the apworld: the
# best possible squad -- as many UNLOCKED wizards as the goal's room actually fields
# (its squadSize, read from the level's wizard prefabs), each contributing 4 abilities minus
# any BASE-KIT ability items still missing (dream specials are upgrades, not extra
# abilities) -- must total at least that many. Use it for goals that need many abilities at
# once. "Use N abilities in one turn" goals get their N read straight from the level file
# automatically (TOTAL_ABILITY_GOAL_PARAMS below); an entry here only raises it.
# Example:
#   "Streets/2 Curfew.lvl|PriestTotalKnockbackGoal": {"abilities": ["UnlockChainShock"], "characters": ["WitchCop"], "totalAbilities": 6},
GOAL_REQUIREMENTS = {
    "Evidence Lockup/2 Pillars.lvl|ClearEnemiesByTurnGoal": {"abilities": ["UnlockChainShock"]},  # Hard to impossible to kill all the guys without chain bolt.
    # Jen's goal, but pushing the warlock into a Death's Door needs Banks fielded
    # (her Death's Door is innate, so the character IS the whole requirement).
    "The Pyromancer/1 The Less Lethal Pyromancer.lvl|DeathsDoorWarlockGoal": {"characters": ["NecroMedic"]},
    "Necro Intro/3 Broom Breach.lvl|DeathsDoorTotalGoal": {"abilities": ["UnlockBroomBreach"]},  # Jen needs broom breach to beat room
    "Necro Intro/3 Broom Breach.lvl|FinishBeforeTurnGoal": {"abilities": ["UnlockBroomBreach"]},  # Jen needs broom breach to beat room
}

def _norm_goal_key(k):
    """Accept both '<level>|<goalName>' and a pasted location key
    'goal:<level>|<goalName>|<ordinal>' -- normalize to the former."""
    if k.startswith("goal:"):
        k = k[len("goal:"):]
    parts = k.split("|")
    if len(parts) >= 3 and parts[-1].isdigit():
        parts = parts[:-1]
    return "|".join(parts)

GOAL_REQUIREMENTS = {_norm_goal_key(k): v for k, v in GOAL_REQUIREMENTS.items()}

# Confidence-goal types whose level file carries a "how many abilities" parameter
# ("<param> = N" inside the .lvl). The generator reads N per level and emits it as that
# goal location's requiredTotalAbilities, so the team-ability gate tracks the game data
# by itself (The Meet 5, Pyromancer 9, Fort Osprey 16, ...).
TOTAL_ABILITY_GOAL_PARAMS = {
    "AbilitiesInOneTurnGoal": "AbilitiesInOneTurnGoalAbilitiesNeeded",
}

# Recommended PER-SLOT perk count per mission = the strongest single wizard the dev
# PerkProgress snapshot expects at that mission (max of <Char>TotalPerkPoints in
# StreamingAssets/PerkProgress/Perk_<Mission>.perks). Per-slot (not team total) because a
# wizard can be fielded in multiple slots, so the binding constraint is your BEST wizard's
# perks, not the sum. Only non-dream checkpoints are listed; other missions carry the last
# value forward in campaign order (see build()). Dreams are skipped (their snapshots bake
# in the +1 dream perks). Values: 1,1,2,2,3,3,4,4,5,5,5,6,6.
PERK_CHECKPOINTS = {
    "Game_Witch_Intro": 1,  "Game_Evidence_Boss": 1,  "Game_Liboli_Intro": 2,
    "Game_Necro_Intro": 2,  "Game_The_Meet": 3,       "Game_The_Pyromancer": 3,
    "Game_Streets": 4,      "Game_The_Recording": 4,  "Game_Siege_Cleric": 5,
    "Game_Kalan_Ambush_2": 5, "Game_Two_Trains": 5,   "Game_Fort_Osprey": 6,
    "Game_Villa_Medil": 6,
}

HERE = os.path.dirname(os.path.abspath(__file__))
# tools/ -> mod -> plugins -> BepInEx -> game root -> _Data/StreamingAssets/Levels
LEVELS_DIR = os.path.normpath(os.path.join(
    HERE, "..", "..", "..", "..", "Tactical Breach Wizards_Data", "StreamingAssets", "Levels"))
# The apworld source package. When present, ap_data.json is copied there automatically so
# the mod and the apworld can never drift apart. (Still rebuild/install afterwards:
# python C:\ProgramData\Archipelago\custom_worlds\_tactical_breach_wizards_apworld\build_apworld.py)
APWORLD_PKG_DIR = r"C:\ProgramData\Archipelago\custom_worlds\_tactical_breach_wizards_apworld\tactical_breach_wizards"

def _level_sort_key(fname):
    # Files look like "1 Entry.lvl", "10 Foo.lvl" -> sort by leading integer when present.
    head = fname.split(" ", 1)[0]
    try:
        return (0, int(head))
    except ValueError:
        return (1, fname.lower())

def mission_levels(folder):
    """Ordered list of .lvl filenames in a mission folder (play order). [] if unreadable."""
    d = os.path.join(LEVELS_DIR, folder)
    try:
        files = [f for f in os.listdir(d) if f.endswith(".lvl")]
    except OSError:
        return []
    return sorted(files, key=_level_sort_key)

# Player-wizard prefab tags as they appear in .lvl files ("Melee Wizard" is Jen; enemy
# variants are named differently, e.g. "Enemy Riot Priest"). The DISTINCT tags present in a
# level = how many wizards that room actually fields (a duplicated prefab -- dream doubles,
# corpses to revive -- adds no unique abilities). Feeds each confidence goal's squadSize.
PLAYER_WIZARD_PREFABS = {
    "Melee Wizard": "WitchCop",
    "Navy Seer": "NavySeer",
    "Necromedic": "NecroMedic",
    "Riot Priest": "RiotPriest",
    "Druid": "Druid",
}

def level_squad_size(level):
    """How many distinct player wizards a level fields (0 if unreadable or none found --
    a few levels use special prefabs, e.g. Rion's dream; the apworld falls back to its
    default squad size for 0)."""
    path = os.path.join(LEVELS_DIR, *level.split("/"))
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            s = f.read()
    except OSError:
        return 0
    return sum(1 for w in PLAYER_WIZARD_PREFABS if f"<prefab:{w}>" in s)

def level_int_param(level, param):
    """Read an integer '<param> = N' line from a level file ("<folder>/<file>.lvl" under
    LEVELS_DIR). 0 if the file or the line is missing."""
    path = os.path.join(LEVELS_DIR, *level.split("/"))
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            for line in f:
                m = re.match(rf"\s*{re.escape(param)}\s*=\s*(\d+)", line)
                if m:
                    return int(m.group(1))
    except OSError:
        pass
    return 0

def load_dump():
    with open(os.path.join(HERE, "tbw_ap_dump.json"), encoding="utf-8") as f:
        return json.load(f)

def build(d):
    acts = d["acts"]

    # --- campaign missions: Gameplay stages with a building folder, in act order ---
    missions = []
    for a in acts:
        for s in a["stages"]:
            if s["type"] == "Gameplay" and s["buildingFolder"]:
                missions.append({
                    "stageID": s["stageID"],
                    "name": s["displayName"] or s["buildingFolder"],
                    "folder": s["buildingFolder"],
                    "act": a["actIndex"],
                    "characterUnlocks": s["characterUnlocks"],
                })
    campaign_folders = {m["folder"] for m in missions}

    # --- confidence goals inside campaign missions, in dump (campaign) order ---
    goals = []
    seen = {}
    for g in d["confidenceGoals"]:
        if g["levelFolder"] not in campaign_folders:
            continue
        key = f'{g["level"]}|{g["goalName"]}'
        seen[key] = seen.get(key, 0) + 1
        ordinal = seen[key]
        # Team-wide ability count this goal needs: auto-read from the level file for
        # known "use N abilities" goal types, raisable via GOAL_REQUIREMENTS. squadSize is
        # how many wizards the room fields (caps how many can contribute abilities).
        total_abilities = int(GOAL_REQUIREMENTS.get(key, {}).get("totalAbilities", 0))
        param = TOTAL_ABILITY_GOAL_PARAMS.get(g["goalName"])
        if param:
            total_abilities = max(total_abilities, level_int_param(g["level"], param))
        squad = level_squad_size(g["level"])
        if total_abilities and squad and total_abilities > squad * 4:
            raise AssertionError(
                f"{key}: needs {total_abilities} abilities but the room only fields "
                f"{squad} wizards (max {squad * 4}) -- the goal would be unreachable")
        auto_abilities, auto_characters = goal_auto_requirements(g["goalName"])
        goals.append({
            "level": g["level"],
            "goalName": g["goalName"],
            "character": g["character"],
            "isFinaleGoal": g["isFinaleGoal"],
            "ordinal": ordinal,   # disambiguates rare duplicate goal types in one level
            "totalAbilities": total_abilities,
            "squadSize": squad,
            "autoAbilities": auto_abilities,
            "autoCharacters": auto_characters,
        })

    # --- outfits: purchasable only (cost > 0, not DLC) ---
    outfits = []
    for entry in d["costumesByCharacter"]:
        ch = entry["character"]
        for o in entry["costumes"]:
            if o["unlockCost"] > 0 and not o["special"]:
                outfits.append({
                    "character": ch,
                    "saveName": o["saveName"],          # "Character:Id"
                    "key": f'{ch}:{o["saveName"]}',
                    "displayName": o["displayName"],
                    "cost": o["unlockCost"],
                })

    # --- special abilities: PerkUnlock stages. Each one is the reward for completing the
    # anxiety-dream mission that immediately precedes it in the campaign (the last Gameplay
    # stage before it in the same act), e.g. Unlock_ChainShockSuperchain <- Game_Lucid_Dream_Jen.
    # PerkUnlock "dream" specials. SeerFinaleKnockBackDummy is dropped: it's a finale-only
    # dummy, not a real grantable perk -- so no item and no ability-unlock location for it
    # (and Zan's dream therefore has no perk reward).
    SKIP_PERKUNLOCK = {"Unlock_SeerFinaleKnockBackDummy"}
    abilities = []
    for a in acts:
        last_gameplay = None
        for s in a["stages"]:
            if s["type"] == "Gameplay":
                last_gameplay = s["stageID"]
            elif s["type"] == "PerkUnlock" and s["stageID"] not in SKIP_PERKUNLOCK:
                abilities.append({"stageID": s["stageID"], "act": a["actIndex"],
                                  "dreamStageID": last_gameplay})

    # --- recruit points: where each character actually joins. Default = first campaign stage
    # that unlocks them (dump), overridden by RECRUIT_OVERRIDE. Built in campaign order so ids
    # stay stable. ---
    mission_by_stage = {m["stageID"]: m for m in missions}
    recruit_stage = {}
    for m in missions:
        for ch in m["characterUnlocks"]:
            recruit_stage.setdefault(ch, m["stageID"])
    recruit_stage.update(RECRUIT_OVERRIDE)  # override wins

    recruits = []
    for m in missions:  # campaign order
        for ch, sid in recruit_stage.items():
            if sid == m["stageID"]:
                recruits.append({"character": ch, "stageID": m["stageID"], "act": m["act"]})

    # --- per-mission logic data: which characters are USED in each mission (from
    # its character-tagged confidence goals), plus firm required-character overrides. ---
    used_by_folder = {}
    for g in goals:
        folder = g["level"].split("/")[0]
        used_by_folder.setdefault(folder, set()).add(g["character"])
    rec_perks = 0  # running recommended team perk total (carry-forward)
    for order, m in enumerate(missions):
        m["order"] = order  # global campaign order (loose-chain hint)
        m["charactersUsed"] = sorted(used_by_folder.get(m["folder"], set()))
        # Region-level character gate = firm requirements UNION the auto-given cast: the mod
        # physically refuses to LAUNCH a mission whose auto-given wizards you don't own, so
        # logic must gate the whole region (halfway, goals, ability unlocks, everything) on
        # them too -- otherwise e.g. Banks's dream's Unlock: Unlock_DeathsFloor looked
        # reachable while the mission itself couldn't be started (user-reported, 2026-07-14).
        m["requiredCharacters"] = sorted(set(FIRM_REQUIRED.get(m["stageID"], []))
                                         | set(AUTO_GIVEN_CHARACTERS.get(m["stageID"], [])))
        m["requiredAbilities"] = FIRM_REQUIRED_ABILITIES.get(m["stageID"], [])
        # OR-gates (see FIRM_REQUIRED_ANY): list of groups, each a list of
        # {"characters","abilities"} alternatives; one alternative per group must be met
        # to complete (requiredAnyOf) / reach the halfway point (halfRequiredAnyOf).
        m["requiredAnyOf"] = FIRM_REQUIRED_ANY.get(m["stageID"], [])
        m["halfRequiredAnyOf"] = HALF_REQUIRED_ANY.get(m["stageID"], [])
        # (The all-characters/all-abilities capstone is NOT folded in here -- see the
        # GOAL_REQUIRES_ALL_* comment; it lives on the victory condition via goal flags.)
        # who is recruited HERE (effective, honoring RECRUIT_OVERRIDE). Informational only:
        # logic does NOT subtract a mission's own recruit from its requirements anymore
        # (controlling a wizard always means having unlocked them first).
        m["recruits"] = [ch for ch, sid in recruit_stage.items() if sid == m["stageID"]]
        lvls = mission_levels(m["folder"])
        m["levels"] = lvls
        m["lastLevel"] = lvls[-1] if lvls else None   # .lvl filename of the mission's final level
        # Halfway checkpoint: the .lvl completed at the ceil(N/2) mark (after room 3 of 6, etc.).
        # Only for missions with >= 2 rooms; single-room missions have no distinct halfway point.
        # halfLevel is always a strictly earlier room than lastLevel (ceil(N/2) < N for N >= 2).
        m["halfLevel"] = lvls[(len(lvls) + 1) // 2 - 1] if len(lvls) >= 2 else None
        m["halfRequiredCharacters"] = HALF_REQUIRED.get(m["stageID"], [])
        m["halfRequiredAbilities"] = HALF_REQUIRED_ABILITIES.get(m["stageID"], [])
        # Recommended PER-SLOT perks for replaying this mission (strongest single wizard the
        # dev snapshot expects). The apworld gates completion/goal checks on the player's BEST
        # unlocked wizard reaching a percent of this (a wizard can fill multiple slots via
        # repetition). Dreams excluded; missions without a checkpoint carry the last value.
        if m["stageID"] in PERK_CHECKPOINTS:
            rec_perks = PERK_CHECKPOINTS[m["stageID"]]
        m["recommendedPerks"] = rec_perks

    # ---------- assign ids ----------
    items, locations = {}, {}

    def add_item(cat, i, name, key, classification, extra=None):
        rec = {"id": ITEM[cat] + i, "name": name, "key": key,
               "category": cat, "classification": classification}
        if extra: rec.update(extra)
        items[key] = rec

    def add_loc(cat, i, name, key, extra=None):
        rec = {"id": LOC[cat] + i, "name": name, "key": key, "category": cat}
        if extra: rec.update(extra)
        locations[key] = rec

    folder_to_mission = {m["folder"]: m for m in missions}

    # items: characters
    for i, ch in enumerate(PLAYABLE):
        add_item("character", i, f"Character: {disp(ch)}", f"char:{ch}", "progression",
                 {"internal": ch})
    # items: mission access
    for i, m in enumerate(missions):
        add_item("mission_access", i, f"Mission Access: {m['name']}",
                 f"missionaccess:{m['stageID']}", "progression",
                 {"stageID": m["stageID"]})
    # items: abilities (PerkUnlock specials) -- ids +1300..+1304 (frozen). A special that
    # stands in for a base-kit ability (SPECIAL_ABILITY_BASEKIT: Death's Floor) gets the
    # friendly name plus the baseKit/character flags so the apworld counts it in Banks's kit.
    for i, ab in enumerate(abilities):
        sid = ab["stageID"]
        if sid in SPECIAL_ABILITY_BASEKIT:
            label, ch = SPECIAL_ABILITY_BASEKIT[sid]
            add_item("ability", i, label, f"ability:{sid}", "progression",
                     {"stageID": sid, "character": ch, "baseKit": True})
        else:
            add_item("ability", i, f"Ability: {sid}",
                     f"ability:{sid}", "progression", {"stageID": sid})
    # items: base-kit abilities (formerly auto-granted at start; now AP-unlocked) -- appended at
    # +1305.. so the specials' ids above stay frozen. No matching location: pure pool items.
    # Classification: progression if a completion/goal requirement references the ability
    # (then the fill must be able to collect it); otherwise useful. The missing-ability perk
    # penalty is CAPPED (see rules.MAX_MISSING_ABILITY_PENALTY), so perks alone can overcome it
    # -- the fill never NEEDS to collect an unreferenced base ability to satisfy a perk gate, so
    # keeping them useful (out of the progression set) keeps generation feasible.
    # EXCEPTION: if any goal carries a team-wide ability count (totalAbilities), the gate can
    # need ANY ability item to raise the team total, so every base-kit ability must be
    # progression for the fill's reachability sweep to satisfy it.
    required_ability_savenames = set()
    for m in missions:  # covers FIRM_REQUIRED_ABILITIES, HALF_*, *_ANY, and the goal's all-abilities gate
        required_ability_savenames.update(m["requiredAbilities"])
        required_ability_savenames.update(m["halfRequiredAbilities"])
        for group in m["requiredAnyOf"] + m["halfRequiredAnyOf"]:
            for alt in group:
                required_ability_savenames.update(alt["abilities"])
    for _req in GOAL_REQUIREMENTS.values():
        required_ability_savenames.update(_req.get("abilities", []))
    for g in goals:
        required_ability_savenames.update(g["autoAbilities"])
    any_team_ability_gate = any(g["totalAbilities"] > 0 for g in goals)
    for j, (sv, label, ch) in enumerate(BASE_ABILITIES):
        if sv in RETIRED_ABILITY_SAVENAMES:
            # Retired: id/key stay defined (frozen contract; the mod keeps blocking its
            # vanilla grant and old seeds can still send it) but no baseKit flag, never
            # progression, never placed (apworld skips retired items in create_items).
            add_item("ability", len(abilities) + j, f"Ability: {label} ({disp(ch)}) [Legacy]",
                     f"ability:{sv}", "useful",
                     {"stageID": sv, "character": ch, "retired": True})
            continue
        cls = "progression" if (any_team_ability_gate or GOAL_REQUIRES_ALL_ABILITIES
                                or sv in required_ability_savenames) else "useful"
        add_item("ability", len(abilities) + j, f"Ability: {label} ({disp(ch)})",
                 f"ability:{sv}", cls, {"stageID": sv, "character": ch, "baseKit": True})
    # items: perk points (one code per character; multiple copies placed at gen time)
    for i, ch in enumerate(PLAYABLE):
        add_item("perk_point", i, f"Perk Point: {disp(ch)}", f"perkpoint:{ch}",
                 "progression", {"character": ch, "internal": ch})
    # items: outfits (filler cosmetics)
    for i, o in enumerate(outfits):
        add_item("outfit", i, f"Outfit: {o['displayName']} ({disp(o['character'])})",
                 f"outfit:{o['key']}", "filler",
                 {"character": o["character"], "saveName": o["saveName"]})
    # items: per-character confidence boosts. The mod blocks natural confidence earning,
    # so these are the ONLY source of confidence (spent at the outfit shop to make the
    # outfit-purchase checks) -> progression. One code per character; copies placed at
    # gen time. The per-boost confidence value is an apworld option sent via slot_data.
    for i, ch in enumerate(PLAYABLE):
        add_item("confidence", i, f"Confidence Boost: {disp(ch)}", f"confidence:{ch}",
                 "progression", {"character": ch, "internal": ch})
    # items: generic junk filler (honest junk; no in-game effect; pads the pool)
    add_item("filler", 0, "Donut", "filler:donut", "filler")

    # locations: mission completions
    for i, m in enumerate(missions):
        add_loc("mission_complete", i, f"Complete: {m['name']}",
                f"mission:{m['stageID']}",
                {"stageID": m["stageID"], "act": m["act"], "order": m["order"]})
    # locations: mission halfway checkpoints (only for multi-room missions). An easier/earlier
    # check than finishing the mission: it lives in the same region and adds only the halfway
    # extras (HALF_REQUIRED*). Its ids live in their own +2700 range, appended in campaign order so
    # they stay stable even if a single-room mission later gains rooms. missionStageID links it to
    # its mission's region (same as confidence goals).
    _half_missions = [m for m in missions if m["halfLevel"]]
    for i, m in enumerate(_half_missions):
        add_loc("mission_half", i, f"Halfway: {m['name']}",
                f"missionhalf:{m['stageID']}",
                {"stageID": m["stageID"], "act": m["act"], "order": m["order"],
                 "missionStageID": m["stageID"],
                 "requiredCharacters": list(m["halfRequiredCharacters"]),
                 "requiredAbilities": list(m["halfRequiredAbilities"]),
                 "requiredAnyOf": list(m["halfRequiredAnyOf"])})
    # locations: confidence goals (linked to their mission + act for region logic)
    for i, g in enumerate(goals):
        folder = g["level"].split("/")[0]
        mis = folder_to_mission.get(folder)
        nm = f"{g['level']} - {g['goalName']}" + (f" #{g['ordinal']}" if g["ordinal"] > 1 else "")
        extra = GOAL_REQUIREMENTS.get(f'{g["level"]}|{g["goalName"]}', {})
        add_loc("confidence_goal", i, f"Goal: {nm} [{disp(g['character'])}]",
                f"goal:{g['level']}|{g['goalName']}|{g['ordinal']}",
                {"level": g["level"], "character": g["character"],
                 "isFinaleGoal": g["isFinaleGoal"],
                 "missionStageID": mis["stageID"] if mis else None,
                 "act": mis["act"] if mis else None,
                 "requiredCharacters": sorted(set(extra.get("characters", [])) | set(g["autoCharacters"])),
                 "requiredAbilities": sorted(set(extra.get("abilities", [])) | set(g["autoAbilities"])),
                 "requiredTotalAbilities": g["totalAbilities"],
                 "squadSize": g["squadSize"]})
    # locations: outfit purchases (available in the meta-menu outfit shop)
    for i, o in enumerate(outfits):
        add_loc("outfit_purchase", i, f"Buy Outfit: {o['displayName']} ({disp(o['character'])})",
                f"buyoutfit:{o['key']}",
                {"character": o["character"], "saveName": o["saveName"], "cost": o["cost"]})
    # locations: ability unlock points (rewarded by the preceding anxiety-dream mission)
    for i, ab in enumerate(abilities):
        add_loc("ability_unlock", i, f"Unlock: {ab['stageID']}",
                f"abilityloc:{ab['stageID']}",
                {"stageID": ab["stageID"], "act": ab["act"], "missionStageID": ab["dreamStageID"]})
    # locations: recruit points
    for i, r in enumerate(recruits):
        add_loc("recruit", i, f"Recruit: {disp(r['character'])}",
                f"recruit:{r['character']}",
                {"character": r["character"], "stageID": r["stageID"], "act": r["act"]})

    # --- sanity: every ability savename / character referenced by any requirement knob must
    # actually exist as a pool item, or the location could never be reached. Catches typos and
    # abilities that don't exist as items (e.g. Death's Door, which is innate). ---
    known_abilities = {it["key"][len("ability:"):] for it in items.values()
                       if it["category"] == "ability" and not it.get("retired")}
    def _check(source, abilities=(), characters=()):
        for sv in abilities:
            if sv in RETIRED_ABILITY_SAVENAMES:
                raise AssertionError(
                    f"{source}: ability {sv!r} is RETIRED (never placed in the pool) -- "
                    "requiring it would make the location unreachable; use its live "
                    "replacement instead (Death's Floor -> Unlock_DeathsFloor)")
            if sv not in known_abilities:
                raise AssertionError(
                    f"{source}: ability {sv!r} is not an AP item "
                    f"(known: {', '.join(sorted(known_abilities))})")
        for ch in characters:
            if ch not in PLAYABLE:
                raise AssertionError(f"{source}: character {ch!r} not in PLAYABLE {PLAYABLE}")
    _check("GOAL_NAME_ABILITY_TOKENS", abilities=GOAL_NAME_ABILITY_TOKENS.values())
    for sid, chs in FIRM_REQUIRED.items():   _check(f"FIRM_REQUIRED[{sid}]", characters=chs)
    for sid, chs in HALF_REQUIRED.items():   _check(f"HALF_REQUIRED[{sid}]", characters=chs)
    for sid, chs in AUTO_GIVEN_CHARACTERS.items():
        _check(f"AUTO_GIVEN_CHARACTERS[{sid}]", characters=chs)
    for sid, abs_ in FIRM_REQUIRED_ABILITIES.items():
        _check(f"FIRM_REQUIRED_ABILITIES[{sid}]", abilities=abs_)
    for sid, abs_ in HALF_REQUIRED_ABILITIES.items():
        _check(f"HALF_REQUIRED_ABILITIES[{sid}]", abilities=abs_)
    # *_ANY alternatives: characters were classified by PLAYABLE membership during
    # normalization, so a typo'd character name lands in "abilities" and fails here loudly.
    for src, knob in (("FIRM_REQUIRED_ANY", FIRM_REQUIRED_ANY),
                      ("HALF_REQUIRED_ANY", HALF_REQUIRED_ANY)):
        for sid, groups in knob.items():
            for gi, group in enumerate(groups):
                for alt in group:
                    _check(f"{src}[{sid}] group {gi} alternative "
                           f"{alt['characters'] + alt['abilities']}",
                           abilities=alt["abilities"])
    for k, req in GOAL_REQUIREMENTS.items():
        _check(f"GOAL_REQUIREMENTS[{k}]", abilities=req.get("abilities", ()),
               characters=req.get("characters", ()))
    # Every knob keyed by stageID must name a real mission (a typo'd key silently no-ops),
    # and HALF_* entries must be on multi-room missions (single-room ones have no halfway
    # location -- use the FIRM_* knobs there instead).
    stage_ids = {m["stageID"] for m in missions}
    multi_room = {m["stageID"] for m in missions if m["halfLevel"]}
    for src, knob in (("FIRM_REQUIRED", FIRM_REQUIRED),
                      ("FIRM_REQUIRED_ABILITIES", FIRM_REQUIRED_ABILITIES),
                      ("FIRM_REQUIRED_ANY", FIRM_REQUIRED_ANY),
                      ("HALF_REQUIRED", HALF_REQUIRED),
                      ("HALF_REQUIRED_ABILITIES", HALF_REQUIRED_ABILITIES),
                      ("HALF_REQUIRED_ANY", HALF_REQUIRED_ANY),
                      ("AUTO_GIVEN_CHARACTERS", AUTO_GIVEN_CHARACTERS),
                      ("PERK_CHECKPOINTS", PERK_CHECKPOINTS)):
        for sid in knob:
            if sid not in stage_ids:
                raise AssertionError(f"{src}: unknown mission stageID {sid!r}")
            if src.startswith("HALF_") and sid not in multi_room:
                raise AssertionError(
                    f"{src}[{sid!r}]: single-room mission has no halfway check -- "
                    "put this requirement in the FIRM_* knob instead")
    # GOAL_REQUIREMENTS keys and name tokens must actually match something, or the intended
    # gate silently never applies.
    goal_keys = {f'{g["level"]}|{g["goalName"]}' for g in goals}
    for k in GOAL_REQUIREMENTS:
        if k not in goal_keys:
            raise AssertionError(f"GOAL_REQUIREMENTS key matches no confidence goal: {k!r}")
    goal_names = {g["goalName"] for g in goals}
    for tok in GOAL_NAME_ABILITY_TOKENS:
        if not any(tok in n for n in goal_names):
            raise AssertionError(
                f"GOAL_NAME_ABILITY_TOKENS[{tok!r}] matches no goal type name "
                "(tokens are case-sensitive substrings)")

    goal_stage = next((m for m in missions if m["stageID"] == GOAL_STAGE_ID), None)
    assert goal_stage, f"GOAL_STAGE_ID {GOAL_STAGE_ID!r} not found among missions: {[m['stageID'] for m in missions]}"

    return {
        "base": BASE,
        "gameName": "Tactical Breach Wizards",
        "playableCharacters": PLAYABLE,
        "nameMap": NAME_MAP,
        "goal": {"stageID": GOAL_STAGE_ID, "name": goal_stage["name"],
                 "locationKey": f"mission:{GOAL_STAGE_ID}",
                 # Victory-condition-only extras (see GOAL_REQUIRES_ALL_* above): the
                 # apworld ANDs these into completion_condition, not location rules.
                 "requireAllCharacters": GOAL_REQUIRES_ALL_CHARACTERS,
                 "requireAllAbilities": GOAL_REQUIRES_ALL_ABILITIES},
        "missionOrder": [m["stageID"] for m in missions],
        "missions": missions,
        "items": list(items.values()),
        "locations": list(locations.values()),
    }

def write_json(data):
    with open(os.path.join(HERE, "ap_data.json"), "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2)

def write_csharp(data):
    lines = [
        "// AUTO-GENERATED by tools/generate_ap_data.py. DO NOT EDIT BY HAND.",
        "// Frozen Archipelago id contract for Tactical Breach Wizards.",
        "using System.Collections.Generic;",
        "",
        "namespace Tactical_Breach_Wizards_Archipelago_Mod",
        "{",
        "    public static class ApData",
        "    {",
        f"        public const long Base = {data['base']};",
        "        // Victory mission. The mod refuses to launch it until every playable character",
        "        // is unlocked (the finale auto-spawns the full squad), mirroring the apworld's",
        "        // region gate. See ApState.IsMissionLaunchable.",
        f"        public const string GoalStageID = {cs(data['goal']['stageID'])};",
        "",
        "        public struct ItemDef { public long Id; public string Name; public string Key; public string Category; public string Classification; }",
        "        public struct LocDef  { public long Id; public string Name; public string Key; public string Category; }",
        "        public struct MissionDef { public string StageID; public string Folder; public string Name; public int Act; public string FirstLevel; public string LastLevel; public string HalfLevel; }",
        "",
        "        public static readonly ItemDef[] Items = new ItemDef[]",
        "        {",
    ]
    for it in data["items"]:
        lines.append(
            f'            new ItemDef {{ Id = {it["id"]}, Name = {cs(it["name"])}, '
            f'Key = {cs(it["key"])}, Category = {cs(it["category"])}, Classification = {cs(it["classification"])} }},')
    lines += ["        };", "",
              "        public static readonly LocDef[] Locations = new LocDef[]", "        {"]
    for lo in data["locations"]:
        lines.append(
            f'            new LocDef {{ Id = {lo["id"]}, Name = {cs(lo["name"])}, '
            f'Key = {cs(lo["key"])}, Category = {cs(lo["category"])} }},')
    lines += ["        };", ""]
    lines += ["        public static readonly MissionDef[] Missions = new MissionDef[]", "        {"]
    for m in data["missions"]:
        first = (m.get("levels") or [None])[0]
        lines.append(
            f'            new MissionDef {{ StageID = {cs(m["stageID"])}, Folder = {cs(m["folder"])}, '
            f'Name = {cs(m["name"])}, Act = {m["act"]}, '
            f'FirstLevel = {cs(first) if first else "null"}, '
            f'LastLevel = {cs(m["lastLevel"]) if m.get("lastLevel") else "null"}, '
            f'HalfLevel = {cs(m["halfLevel"]) if m.get("halfLevel") else "null"} }},')
    lines += ["        };", ""]
    # Mission -> character it recruits (the contract's recruit locations, which already honour
    # RECRUIT_OVERRIDE). The mod fires the recruit check when this mission completes; keeping it
    # generated here means moving a recruit point can never drift from the apworld's logic.
    lines += ["        // stageID -> internal character recruited by completing that mission.",
              "        public static readonly Dictionary<string, string> RecruitByMission = new Dictionary<string, string>",
              "        {"]
    for lo in data["locations"]:
        if lo["category"] == "recruit":
            lines.append(f'            {{ {cs(lo["stageID"])}, {cs(lo["character"])} }},')
    lines += ["        };", ""]
    # internal -> display names (for user-facing "missing: Jen, Zan" messages).
    lines += ["        // internal character name -> in-game display name.",
              "        public static readonly Dictionary<string, string> CharacterDisplayNames = new Dictionary<string, string>",
              "        {"]
    for ch in data["playableCharacters"]:
        lines.append(f'            {{ {cs(ch)}, {cs(data["nameMap"].get(ch, ch))} }},')
    lines += ["        };", ""]
    # In-game LAUNCH gates the mod enforces: ONLY the characters a mission auto-gives you
    # (AUTO_GIVEN_CHARACTERS + a dream's own wizard). Requirements that are merely needed
    # to PROGRESS (objective wizards, abilities) do NOT block launching -- matching logic.
    rev_names = {v: k for k, v in data["nameMap"].items()}
    lines += ["        // stageID -> characters that must be UNLOCKED before the mod lets you LAUNCH the",
              "        // mission: only wizards the mission AUTO-GIVES you to control (scripted casts,",
              "        // dream squads). Progress-only requirements don't block launch. See",
              "        // ApState.IsMissionLaunchable.",
              "        public static readonly Dictionary<string, string[]> LaunchRequiredCharacters = new Dictionary<string, string[]>",
              "        {"]
    for m in data["missions"]:
        sid = m["stageID"]
        req = set(AUTO_GIVEN_CHARACTERS.get(sid, []))
        if sid.startswith("Game_Lucid_Dream_"):
            req.add(rev_names[sid[len("Game_Lucid_Dream_"):]])
        if not req:
            continue
        arr = ", ".join(cs(c) for c in data["playableCharacters"] if c in req)
        lines.append(f'            {{ {cs(sid)}, new[] {{ {arr} }} }},')
    lines += ["        };", ""]
    # Dream completion == its PerkUnlock reward point. Vanilla runs the PerkUnlock stage right
    # after the dream; the AP hub plays missions via flashback where that stage never runs, so
    # the mod fires the ability-unlock location itself when the dream's completion check fires.
    lines += ["        // dream mission stageID -> PerkUnlock stageID whose ability-unlock location",
              "        // fires when that dream mission completes.",
              "        public static readonly Dictionary<string, string> AbilityUnlockByMission = new Dictionary<string, string>",
              "        {"]
    for lo in data["locations"]:
        if lo["category"] == "ability_unlock" and lo.get("missionStageID"):
            lines.append(f'            {{ {cs(lo["missionStageID"])}, {cs(lo["stageID"])} }},')
    lines += ["        };", "    }", "}", ""]
    out = os.path.join(HERE, "..", "ApData.cs")
    with open(os.path.normpath(out), "w", encoding="utf-8") as f:
        f.write("\n".join(lines))

def cs(s):  # C# string literal
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'

def write_python(data):
    lines = ["# AUTO-GENERATED by tools/generate_ap_data.py. DO NOT EDIT BY HAND.",
             "# Frozen Archipelago id contract for Tactical Breach Wizards.", "",
             f"BASE = {data['base']}",
             f"PLAYABLE_CHARACTERS = {data['playableCharacters']!r}", "",
             "# (name, id, key, category, classification)",
             "ITEMS = ["]
    for it in data["items"]:
        lines.append(f'    ({it["name"]!r}, {it["id"]}, {it["key"]!r}, {it["category"]!r}, {it["classification"]!r}),')
    lines += ["]", "", "# (name, id, key, category)", "LOCATIONS = ["]
    for lo in data["locations"]:
        lines.append(f'    ({lo["name"]!r}, {lo["id"]}, {lo["key"]!r}, {lo["category"]!r}),')
    lines += ["]", ""]
    with open(os.path.join(HERE, "ap_data.py"), "w", encoding="utf-8") as f:
        f.write("\n".join(lines))

def main():
    d = load_dump()
    data = build(d)
    write_json(data); write_csharp(data); write_python(data)
    import collections
    ic = collections.Counter(i["category"] for i in data["items"])
    lc = collections.Counter(l["category"] for l in data["locations"])
    print("ITEM codes:", dict(ic), "=", len(data["items"]))
    print("LOCATIONS :", dict(lc), "=", len(data["locations"]))
    gated = {}
    for lo in data["locations"]:
        if lo["category"] == "confidence_goal" and lo.get("requiredAbilities"):
            gname = lo["key"].rsplit("|", 2)[1]
            e = gated.setdefault(gname, [set(), 0])
            e[0].update(lo["requiredAbilities"]); e[1] += 1
    if gated:
        print("Ability-gated confidence goals (auto + manual):")
        for gname in sorted(gated):
            abilities, n = gated[gname]
            print(f"  {gname} x{n} -> {', '.join(sorted(abilities))}")
    print("Wrote ap_data.json, ApData.cs, ap_data.py")
    if os.path.isdir(APWORLD_PKG_DIR):
        import shutil
        shutil.copyfile(os.path.join(HERE, "ap_data.json"),
                        os.path.join(APWORLD_PKG_DIR, "ap_data.json"))
        print(f"Copied ap_data.json -> {APWORLD_PKG_DIR}")
        print("Now test + rebuild/install the apworld:")
        print(r"  python C:\ProgramData\Archipelago\custom_worlds\_tactical_breach_wizards_apworld\test_world.py")
        print(r"  python C:\ProgramData\Archipelago\custom_worlds\_tactical_breach_wizards_apworld\build_apworld.py")
    else:
        print(f"NOTE: apworld package not found at {APWORLD_PKG_DIR}; "
              "copy ap_data.json there yourself.")

if __name__ == "__main__":
    main()
