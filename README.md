# Tactical Breach Wizards — Archipelago Mod

An [Archipelago](https://archipelago.gg) multiworld randomizer for Tactical Breach
Wizards. Wizards, missions, abilities, perk points, and confidence are shuffled into
the multiworld item pool; you send checks by completing missions, reaching mid-mission
checkpoints, hitting confidence goals, buying outfits, and recruiting wizards.

This README covers installing and playing the **game mod**. Everything on the
Archipelago server side (installing the apworld, YAML options, generating a
multiworld) is documented in the apworld repo:
**[Linkr98/tactical_breach_wizards_apworld](https://github.com/Linkr98/tactical_breach_wizards_apworld)**

---

## Requirements

- Tactical Breach Wizards (Steam, Windows)
- [BepInEx 5.x](https://github.com/BepInEx/BepInEx/releases) (x64) — the mod loader
- This mod's release files
- The apworld installed into Archipelago (see the
  [apworld README](https://github.com/Linkr98/tactical_breach_wizards_apworld))

## Installing

1. **Install BepInEx** into the game folder
   (`...\Steam\steamapps\common\Tactical Breach Wizards\`): download the BepInEx 5.x
   x64 zip and extract it so `winhttp.dll` and the `BepInEx\` folder sit next to the
   game's exe. Launch the game once and quit — this lets BepInEx generate its folders.
2. **Install the mod**: put the mod's files into a folder under `BepInEx\plugins\`
   (any subfolder works). These four files must sit together:
   - `Tactical Breach Wizards Archipelago Mod.dll`
   - `Archipelago.MultiClient.Net.dll`
   - `websocket-sharp.dll`
   - `ap_icon.png`
3. That's it for the game side. To generate or host a multiworld, follow the
   [apworld README](https://github.com/Linkr98/tactical_breach_wizards_apworld).

## Connecting & playing

1. Start the game. When you click Play / start / continue a save, the **connect
   dialog** opens — enter the server host, port, your slot name, and password.
   Last-used values are remembered (stored in `BepInEx\config\com.lincoln.tbwap.cfg`).
2. You start with one random wizard (base kit + 1 perk point) and one random early
   mission. Everything else — the other wizards, mission access, abilities, perk
   points, and Confidence Boosts — arrives as items from the multiworld.
3. **Goal:** complete *Counterheist: The Roof*.

In-game keys:

| Key | What it does |
|---|---|
| **F9** | Open/close the connect panel manually |
| **F10** | Mission hub: launch any unlocked mission, see recent AP activity |

Unlocked missions also appear in the game's own mission-replay screen.

Good to know:

- Confidence and perk points can **only** come from AP items — natural earning is
  disabled, so don't worry when goals stop paying out directly.
- **Halfway checkpoints:** once you've reached a mission's halfway point (the check
  fires mid-mission), its level card in the replay screen reveals the rooms up to the
  start of the back half — click one to resume from there instead of redoing the
  first half. The run continues through to the end of the mission.
- **Perk respec is always available** (normally a post-game feature): refund any
  purchased perk from the perks screen between missions and re-spend the points.
- Outfits aren't items: buying one at the shop *is* the check, and you keep the
  cosmetic.
- Your AP unlocks are re-applied from the server every time you connect, so the mod
  can't corrupt a local save.

## Troubleshooting

- The mod logs to `BepInEx\LogOutput.log` — look for
  `Tactical Breach Wizards Archipelago` lines to confirm it loaded.
- If the connect dialog never appears, press **F9**; if that does nothing, the mod
  didn't load — check that all three files from step 2 are in `BepInEx\plugins\` and
  that BepInEx itself is running (the log file exists and is fresh).

---

Modding/development documentation lives in [INFO.md](INFO.md).
