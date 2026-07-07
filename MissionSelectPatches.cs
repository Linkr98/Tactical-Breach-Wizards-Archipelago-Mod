using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
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
    /// Makes AP-unlocked campaign missions launchable from the in-campaign replay board
    /// (ChapterSelectPanel in chapterSelect mode -- the screen you reach between missions). Vanilla
    /// reveals polaroids only for the COMPLETED linear prefix (reveal is a count from the start of
    /// each chapter line, so it can't surface the out-of-order missions AP unlocks). After the panel
    /// builds, we directly Show() the polaroid of every AP-unlocked mission and mark its line as
    /// finished-displaying so the polaroid is clickable; clicking opens the level card and launches
    /// via LoadFlashbackMission (the same path completed replays use). Missions you don't yet hold
    /// the AP access item for stay hidden, so they can't be launched. Restricted to chapterSelect
    /// mode so it never disturbs the new-game / chapter-intro reveal animations.
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
                        if (!state.IsMissionUnlocked(pol.myStage.stageID)) continue;
                        // Show the real mission photo instead of the "unreached" silhouette overlay:
                        // Show() only draws the overlay when isCompleted is false. In AP the mission is
                        // reachable (you hold its access item), so the actual icon is the right visual.
                        pol.isCompleted = true;
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
    [HarmonyPatch(typeof(ChapterSelectLevelsCard), "SetStageID")]
    public static class Patch_BlockRoomSkip
    {
        [HarmonyPostfix]
        public static void Postfix(ChapterSelectLevelsCard __instance, string _stageID)
        {
            if (!ApManager.IsActive || __instance.levelLinesContainer == null) return;
            // Done (AP mission-complete check fired, or game flag) -> allow individual room replay so
            // the player can revisit rooms for missed confidence goals. AP missions finish via
            // flashback (no game stage-complete flag), so IsMissionCompleted is the reliable signal.
            if (ApManager.IsMissionCompleted(_stageID) || Managers.Stage.IsStageCompleted(_stageID)) return;
            foreach (Transform child in __instance.levelLinesContainer)
                child.gameObject.SetActive(false);                   // not done -> force Play All (start to finish)
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
