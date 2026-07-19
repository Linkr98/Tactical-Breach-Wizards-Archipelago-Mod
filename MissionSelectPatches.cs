using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Wizards.SaveSystem;
using Wizards.Stages;
using Wizards.UI;

namespace Tactical_Breach_Wizards_Archipelago_Mod
{
    /// <summary>
    /// Makes AP-unlocked campaign missions show up in the game's native replay screen
    /// (MissionSelectPanel), so the player can launch them there instead of the F10 hub. Vanilla
    /// only lists COMPLETED gameplay stages (Managers.Stage.ReplayableGameplayStages, which also
    /// drops Prologue/Flashback); in AP you can play any mission you hold the mission-access item
    /// for, in any order, so we append every unlocked mission vanilla didn't already list. The
    /// instantiated panel builds purely from the stage's level files and launches via
    /// LoadFlashbackMission -- the same path completed replays use -- so never-completed missions
    /// display and launch fine. (Children are wiped + rebuilt on each OnActivatePanel, so the
    /// appended panels clean themselves up; we don't touch the panel's private tracking list.)
    /// </summary>
    /// <summary>
    /// Shows EVERY campaign mission on the in-campaign replay board (ChapterSelectPanel in
    /// chapterSelect mode) so the whole seed is visible at a glance: launchable missions show
    /// their real photo; everything else (missing its access item, or missing wizards the
    /// mission would put in your hands) shows the darkened "unreached" silhouette as the locked
    /// cue. Vanilla only reveals the COMPLETED linear prefix (reveal is a count from the start
    /// of each chapter line), and would show the FIRST mission's real photo even with nothing
    /// unlocked -- both replaced here. Every polaroid stays clickable: the level card then hides
    /// Play All for locked missions and toasts exactly what's missing, and OnPlayAllClick is
    /// hard-blocked as backstop. Restricted to chapterSelect mode so it never disturbs the
    /// new-game / chapter-intro reveal animations.
    /// </summary>
    [HarmonyPatch(typeof(ChapterSelectPanel), "OnActivatePanel")]
    public static class Patch_ChapterReplayShowApUnlocked
    {
        [HarmonyPostfix]
        public static void Postfix(ChapterSelectPanel __instance)
        {
            if (!ApManager.IsActive || !__instance.ChapterSelect) return;
            var state = ApManager.State;
            if (state == null || __instance.chapterLines == null) return;
            try
            {
                int shown = 0;
                foreach (var line in __instance.chapterLines)
                {
                    if (line?.polaroids == null) continue;
                    bool revealedInLine = false;
                    foreach (var pol in line.polaroids)
                    {
                        if (pol?.myStage == null) continue;
                        // Only the 29 AP campaign missions; other stage polaroids stay vanilla.
                        if (!ApLookup.MissionByStageId.ContainsKey(pol.myStage.stageID)) continue;
                        // Real photo iff launchable (isCompleted=true skips the "unreached"
                        // silhouette overlay); locked missions -- missing their access item or
                        // wizards the mission puts in your hands -- keep the silhouette. This
                        // also OVERRIDES vanilla's reveal of the first mission, which would
                        // otherwise show its real photo before it's unlocked. Clicking a locked
                        // polaroid opens the level card, which hides Play All and toasts what's
                        // missing.
                        pol.isCompleted = state.IsMissionLaunchable(pol.myStage.stageID);
                        pol.Show(false);
                        revealedInLine = true;
                        shown++;
                    }
                    if (revealedInLine)
                    {
                        // The click handler requires the line be shown + done animating.
                        line.canBeShown = true;
                        line.finishedDisplaying = true;
                        // Bake the row's layout. Vanilla disables the HorizontalLayoutGroup once a line
                        // finishes its reveal animation; lines we reveal out-of-order never hit that
                        // path, so their layout group stays live and hovering a polaroid (which calls
                        // SetAsLastSibling) reflows the whole row. Freeze positions here to stop it.
                        FreezeLayout(line.polaroidParent);
                        FreezeLayout(line.additionalPolaroidParent);
                    }
                }
                MainMod.Logger.LogInfo($"[AP] Chapter replay board: revealed {shown} AP-unlocked mission polaroid(s).");
            }
            catch (System.Exception e)
            {
                MainMod.Logger.LogWarning($"[AP] ChapterSelect populate error: {e.Message}");
            }
        }

        // Compute the row's final positions once, then disable the layout group so subsequent sibling
        // reordering (from hover) no longer moves the polaroids.
        private static void FreezeLayout(Transform parent)
        {
            if (parent == null) return;
            var hlg = parent.GetComponent<HorizontalLayoutGroup>();
            if (hlg == null || !hlg.enabled) return;
            if (parent is RectTransform rt)
                LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
            hlg.enabled = false;
        }
    }

    /// <summary>
    /// Stops the player skipping ahead within a mission. The chapter level card lists every room
    /// (level) of a mission as its own launchable line, so you could jump straight to the last room
    /// and trip the mission-complete check (which fires on the last level) without playing the
    /// earlier rooms. In AP mode, for a mission that isn't completed yet, we hide the per-room lines
    /// and leave only "Play All" -- which runs the mission from the first room through the last
    /// (LoadChapterSelectStage -> LoadFlashbackMission). Already-completed missions keep their per-
    /// room replay (no skip exploit remains once it's done). Only the campaign card (SetStageID) is
    /// touched; dream missions use SetDirectory and AP excludes them anyway.
    /// </summary>
    /// <summary>
    /// Shared launch-gating for the mission level card: hides the "Play All" button and toasts
    /// exactly what's missing when the mission isn't launchable yet (no access item; anxiety
    /// dream without its wizard; the tutorial without Zan; the finale without the full squad).
    /// The card object is reused between opens, so the button must be re-enabled for launchable
    /// missions. Room-line hiding (anti-skip) is handled by the callers.
    /// </summary>
    internal static class LaunchGate
    {
        public static void Apply(ChapterSelectLevelsCard card, string stageID, string displayName)
        {
            var state = ApManager.State;
            if (state == null || card.playAllButton == null) return;
            var missing = state.MissingLaunchRequirements(stageID);
            card.playAllButton.gameObject.SetActive(missing.Count == 0);
            if (missing.Count > 0)
                ApManager.ToastInfo($"{displayName} is locked -- missing: {string.Join(", ", missing.ToArray())}");
        }
    }

    [HarmonyPatch(typeof(ChapterSelectLevelsCard), "SetStageID")]
    public static class Patch_BlockRoomSkip
    {
        private static readonly AccessTools.FieldRef<ChapterSelectLevelsCard, LoadableStage> StageRef =
            AccessTools.FieldRefAccess<ChapterSelectLevelsCard, LoadableStage>("stage");

        [HarmonyPostfix]
        public static void Postfix(ChapterSelectLevelsCard __instance, string _stageID)
        {
            if (!ApManager.IsActive || __instance.levelLinesContainer == null) return;
            // Not launchable (e.g. the vanilla-revealed tutorial without its access item / Zan,
            // or the finale without the roster) -> no Play All either; toast says what's missing.
            bool known = ApLookup.MissionByStageId.TryGetValue(_stageID, out var mission);
            LaunchGate.Apply(__instance, _stageID, known ? mission.Name : _stageID);
            // Done (AP mission-complete check fired, or game flag) -> allow individual room replay so
            // the player can revisit rooms for missed confidence goals. AP missions finish via
            // flashback (no game stage-complete flag), so IsMissionCompleted is the reliable signal.
            if (ApManager.IsMissionCompleted(_stageID) || Managers.Stage.IsStageCompleted(_stageID)) return;
            var stage = StageRef(__instance);
            RoomGate.HideUnreachedRooms(__instance.levelLinesContainer,
                stage != null ? stage.GetStageBuildingFolder() : (LevelFile?)null,
                known ? mission.HalfLevel : null, known ? mission.StageID : null);
        }
    }

    /// <summary>
    /// Room-line gating for missions that aren't completed yet ("resume from checkpoint"). Once a
    /// mission's Halfway check is registered with AP, the card reveals the room lines up to and
    /// including the FIRST ROOM OF THE BACK HALF -- clicking a room runs the mission from there
    /// through to the last room (the exact same flashback path Play All uses, just a different
    /// starting level), so the player can resume without redoing the first half. Rooms beyond that
    /// stay hidden: jumping ahead would cheese the mission-complete check, which fires on the last
    /// room. Before halfway (or for single-room missions, which have no half location), every line
    /// stays hidden and Play All remains the only way in. Lines are matched by their LevelFile
    /// rather than child order, since the card's old lines are still awaiting destruction when the
    /// new ones are generated.
    /// </summary>
    internal static class RoomGate
    {
        private static readonly AccessTools.FieldRef<ChapterSelectLevelLine, LevelFile> LineLevel =
            AccessTools.FieldRefAccess<ChapterSelectLevelLine, LevelFile>("level");

        public static void HideUnreachedRooms(Transform container, LevelFile? roomsFolder, string halfLevel, string stageID)
        {
            var visible = new System.Collections.Generic.HashSet<string>();
            var state = ApManager.State;
            bool launchable = state != null && stageID != null && state.MissingLaunchRequirements(stageID).Count == 0;
            if (launchable && halfLevel != null && roomsFolder.HasValue && ApManager.IsMissionHalfReached(stageID))
            {
                var rooms = LevelFile.GetLevelFiles(roomsFolder.Value);
                for (int i = 0; i < rooms.Length; i++)
                {
                    visible.Add(rooms[i].FileName);
                    if (rooms[i].FileName == halfLevel)
                    {
                        if (i + 1 < rooms.Length) visible.Add(rooms[i + 1].FileName);
                        break;
                    }
                }
            }
            foreach (Transform child in container)
            {
                var line = child.GetComponent<ChapterSelectLevelLine>();
                bool show = false;
                if (line != null)
                {
                    LevelFile lvl = LineLevel(line);
                    show = visible.Contains(lvl.FileName);
                }
                child.gameObject.SetActive(show);
            }
        }
    }

    /// <summary>
    /// Same treatment for directory-based cards (the anxiety-dreams panel uses SetDirectory):
    /// gate Play All + toast what's missing, and apply the same anti-room-skip as campaign
    /// missions (dream completion fires on the LAST room, so jumping ahead would cheese it).
    /// Folders that aren't AP campaign missions (custom levels) are left vanilla.
    /// </summary>
    [HarmonyPatch(typeof(ChapterSelectLevelsCard), "SetDirectory")]
    public static class Patch_GateDreamCard
    {
        [HarmonyPostfix]
        public static void Postfix(ChapterSelectLevelsCard __instance, LevelFile _dir)
        {
            if (!ApManager.IsActive || _dir == null) return;
            if (!ApLookup.MissionByFolder.TryGetValue(_dir.folder, out var mission)) return;
            LaunchGate.Apply(__instance, mission.StageID, mission.Name);
            if (ApManager.IsMissionCompleted(mission.StageID)) return;
            if (__instance.levelLinesContainer == null) return;
            RoomGate.HideUnreachedRooms(__instance.levelLinesContainer, _dir, mission.HalfLevel, mission.StageID);
        }
    }

    /// <summary>
    /// Hard block behind the card UI: even if Play All is somehow pressed (gamepad focus, a
    /// path that skipped our SetStageID/SetDirectory gating), a non-launchable mission refuses
    /// to start and toasts the reason. Reads the card's private stage/directory fields.
    /// </summary>
    [HarmonyPatch(typeof(ChapterSelectLevelsCard), "OnPlayAllClick")]
    public static class Patch_BlockLockedPlayAll
    {
        private static readonly AccessTools.FieldRef<ChapterSelectLevelsCard, LoadableStage> StageRef =
            AccessTools.FieldRefAccess<ChapterSelectLevelsCard, LoadableStage>("stage");
        private static readonly AccessTools.FieldRef<ChapterSelectLevelsCard, LevelFile> DirRef =
            AccessTools.FieldRefAccess<ChapterSelectLevelsCard, LevelFile>("directory");

        [HarmonyPrefix]
        public static bool Prefix(ChapterSelectLevelsCard __instance)
        {
            if (!ApManager.IsActive) return true;
            var state = ApManager.State;
            if (state == null) return true;
            string stageID = null, name = null;
            var stage = StageRef(__instance);
            if (stage != null)
            {
                stageID = stage.stageID;
                name = string.IsNullOrEmpty(stage.displayName) ? stage.stageID : stage.displayName;
            }
            else
            {
                var dir = DirRef(__instance);
                if (dir != null && ApLookup.MissionByFolder.TryGetValue(dir.folder, out var mission))
                {
                    stageID = mission.StageID;
                    name = mission.Name;
                }
            }
            if (stageID == null) return true;   // custom/non-AP content -> vanilla
            var missing = state.MissingLaunchRequirements(stageID);
            if (missing.Count == 0) return true;
            MainMod.Logger.LogWarning($"[AP] Play All on {stageID} blocked: missing {string.Join(", ", missing.ToArray())}.");
            ApManager.ToastInfo($"{name} is locked -- missing: {string.Join(", ", missing.ToArray())}");
            return false;
        }
    }

    /// <summary>
    /// In AP mode, the mission character-select "Default" button must not be usable: it loads the
    /// mission with the default story squad, which can include characters you don't own. We hide
    /// the button and also neutralize its action as a safety (gamepad / other input paths).
    /// </summary>
    [HarmonyPatch(typeof(MissionCharacterSelectPanel), "OnActivatePanel")]
    public static class Patch_HideDefaultSquadButton
    {
        [HarmonyPostfix]
        public static void Postfix(MissionCharacterSelectPanel __instance)
        {
            if (!ApManager.IsActive) return;
            if (__instance.DefaultButton != null)
                __instance.DefaultButton.gameObject.SetActive(false);
        }
    }

    [HarmonyPatch(typeof(MissionCharacterSelectPanel), "OnDefault")]
    public static class Patch_BlockDefaultSquad
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (!ApManager.IsActive) return true;   // offline -> vanilla
            MainMod.Logger.LogInfo("[AP] 'Default' squad button blocked (would field characters you may not own).");
            return false;                            // skip original
        }
    }

    /// <summary>
    /// When the outfit shop opens, scout every outfit-purchase location so the entries can show which
    /// AP item each purchase releases, and create a one-time server hint for each. Buying an outfit is
    /// an AP location check, so this tells the player (and their multiworld) what's behind each one.
    /// </summary>
    [HarmonyPatch(typeof(OutfitsPanel), "OnActivatePanel")]
    public static class Patch_OutfitShopOpened
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            if (!ApManager.IsActive) return;
            try { ApManager.OnOutfitShopOpened(); }
            catch (System.Exception e) { MainMod.Logger.LogWarning($"[AP] Outfit shop open hook error: {e.Message}"); }
        }
    }

    /// <summary>
    /// Renames each outfit entry in the shop to the AP item its purchase releases (from the scouted
    /// data filled in by <see cref="Patch_OutfitShopOpened"/>). Runs in the entry's per-frame Update so
    /// the label updates as soon as the async scout returns, and stays correct across selection changes.
    /// Free defaults / DLC costumes aren't AP checks, so they keep their vanilla name.
    /// </summary>
    [HarmonyPatch(typeof(OutfitCostumeEntry), "Update")]
    public static class Patch_OutfitEntryRename
    {
        [HarmonyPostfix]
        public static void Postfix(OutfitCostumeEntry __instance)
        {
            if (!ApManager.IsActive || __instance.costume == null) return;
            var button = __instance.costumeButton;
            if (button == null || button.myText == null) return;
            string apName = ApManager.OutfitDisplayName(__instance.character.ToString(), __instance.costume.saveName);
            if (apName != null && button.myText.text != apName)
                button.myText.text = apName;
        }
    }
}
