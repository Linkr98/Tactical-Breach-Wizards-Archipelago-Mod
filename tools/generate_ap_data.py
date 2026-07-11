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
    ("UnlockChainShock",   "Chain Bolt",     "WitchCop"),
    ("UnlockGhostShot",    "Spectral Skull",      "NecroMedic"),
    ("UnlockTransference", "Transference",    "NecroMedic"),
    ("UnlockDeathsFloor",  "Death's Floor",   "NecroMedic"),
    ("UnlockCrowdGrenade", "Spore Grenade",   "Druid"),
]

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

# Firm per-mission character requirements (user-specified). Everything else is left
# to the apworld's loose logic (see charactersUsed hint). A mission's own recruit is NOT
# exempt: if you control a wizard in a mission, you must have unlocked them first, even
# the one that mission would normally hand you (project owner's rule, 2026-07-07).
FIRM_REQUIRED = {


    # Zan with false Prophet is needed to bait the turret to complete room 2.
    # (The Blacksite's stageID is Game_Liboli_Intro, not Game_The_Blacksite.)
    "Game_Liboli_Intro": ["NavySeer"],
    "Game_Necro_Intro": ["NecroMedic", "WitchCop"],
    
    "Game_Train": ["WitchCop"], #Objective Jen must reach the Green Zone 
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
    
    "Game_Liboli_Intro": ["UnlockFalseProphet"],   # Blacksite: bait the turret in room 2
    "Game_Necro_Intro": ["UnlockBroomBreach"], # Need this for 1 of the rooms, Broom breach tutorial
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
    "Game_Lucid_Dream_Banks": ["NecroMedic", "NavySeer", "WitchCop", "RiotPriest", "Druid"],  # All characters show as dead in the dream and can be revived
    "Game_Siege_Cleric": ["NecroMedic"],  # Siege Cleric L1 has a required ResurrectDruidObjective -> needs Banks's resurrect.
}
HALF_REQUIRED_ABILITIES = {
}

# Extra requirements for a specific CONFIDENCE GOAL, keyed by "<level>|<goalName>"
# (ordinal-agnostic; applies to all copies of that goal). Each goal already implicitly needs
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
}

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
        goals.append({
            "level": g["level"],
            "goalName": g["goalName"],
            "character": g["character"],
            "isFinaleGoal": g["isFinaleGoal"],
            "ordinal": ordinal,   # disambiguates rare duplicate goal types in one level
            "totalAbilities": total_abilities,
            "squadSize": squad,
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
        m["requiredCharacters"] = FIRM_REQUIRED.get(m["stageID"], [])
        m["requiredAbilities"] = FIRM_REQUIRED_ABILITIES.get(m["stageID"], [])
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
    # items: abilities (PerkUnlock specials) -- ids +1300..+1304 (frozen)
    for i, ab in enumerate(abilities):
        add_item("ability", i, f"Ability: {ab['stageID']}",
                 f"ability:{ab['stageID']}", "progression", {"stageID": ab["stageID"]})
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
    for _abs in FIRM_REQUIRED_ABILITIES.values():
        required_ability_savenames.update(_abs)
    for _req in GOAL_REQUIREMENTS.values():
        required_ability_savenames.update(_req.get("abilities", []))
    any_team_ability_gate = any(g["totalAbilities"] > 0 for g in goals)
    for j, (sv, label, ch) in enumerate(BASE_ABILITIES):
        cls = "progression" if (any_team_ability_gate or sv in required_ability_savenames) else "useful"
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
                 "requiredAbilities": list(m["halfRequiredAbilities"])})
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
                 "requiredCharacters": list(extra.get("characters", [])),
                 "requiredAbilities": list(extra.get("abilities", [])),
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

    goal_stage = next((m for m in missions if m["stageID"] == GOAL_STAGE_ID), None)
    assert goal_stage, f"GOAL_STAGE_ID {GOAL_STAGE_ID!r} not found among missions: {[m['stageID'] for m in missions]}"

    return {
        "base": BASE,
        "gameName": "Tactical Breach Wizards",
        "playableCharacters": PLAYABLE,
        "nameMap": NAME_MAP,
        "goal": {"stageID": GOAL_STAGE_ID, "name": goal_stage["name"],
                 "locationKey": f"mission:{GOAL_STAGE_ID}"},
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
