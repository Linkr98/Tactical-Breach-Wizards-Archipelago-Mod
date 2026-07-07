using HarmonyLib;
using Wizards.LevelBuilding;
using Wizards.People;
using Wizards.SaveSystem;

namespace Tactical_Breach_Wizards_Archipelago_Mod
{
    /// <summary>
    /// Blocks natural confidence earning while connected to AP, so confidence comes ONLY from
    /// "Confidence Boost: &lt;Char&gt;" AP items (granted via <see cref="ApManager.GrantConfidence"/>).
    /// AddPoints is the single positive-earning entry point (goals call it with +1); outfit purchases
    /// subtract via `confidencePoints[_name] -= _cost` directly, NOT through AddPoints, so they're
    /// unaffected. We let our own grant through with a gate flag and allow non-positive amounts
    /// (refunds) so nothing else breaks.
    /// </summary>
    [HarmonyPatch(typeof(ConfidencePointManager), "AddPoints")]
    public static class Patch_AddPoints
    {
        public static bool ApGrantInProgress;

        [HarmonyPrefix]
        public static bool Prefix(int _amount)
        {
            if (!ApManager.IsActive) return true;   // vanilla untouched when offline
            if (ApGrantInProgress) return true;     // our own AP grant -> allow
            if (_amount <= 0) return true;          // refunds / decrements pass through
            return false;                           // block all natural positive earning
        }
    }

    /// <summary>
    /// Persists the per-character AP-granted confidence totals inside ConfidencePointManager's own
    /// save block, so they share the game save's lifecycle and stay bound to the exact slot. This is
    /// what makes confidence grants delta-based and reconnect/reload-safe (re-sent items don't
    /// re-grant). See <see cref="ApManager.SaveConfidenceGranted"/> / LoadConfidenceGranted.
    /// </summary>
    [HarmonyPatch(typeof(ConfidencePointManager), "SaveData")]
    public static class Patch_Confidence_SaveData
    {
        [HarmonyPostfix]
        public static void Postfix(IDataBlockRecorder _writer)
        {
            try { ApManager.SaveConfidenceGranted(_writer); }
            catch (System.Exception e) { MainMod.Logger.LogWarning($"[AP] SaveConfidenceGranted error: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(ConfidencePointManager), "LoadData")]
    public static class Patch_Confidence_LoadData
    {
        [HarmonyPostfix]
        public static void Postfix(DataBlock _data)
        {
            try { ApManager.LoadConfidenceGranted(_data); }
            catch (System.Exception e) { MainMod.Logger.LogWarning($"[AP] LoadConfidenceGranted error: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(ConfidencePointManager), "ClearSaveData")]
    public static class Patch_Confidence_ClearSaveData
    {
        [HarmonyPostfix]
        public static void Postfix() => ApManager.ClearConfidenceGranted();
    }

    /// <summary>
    /// Fires the outfit-purchase location check when the player buys a costume. UnlockCostume is
    /// the single purchase entry point (OutfitCostumeEntry calls it with the costume's saveName, the
    /// bare id like "1B"/"Desert"); it returns true only on a successful buy. The AP location key is
    /// buyoutfit:&lt;internalChar&gt;:&lt;saveName&gt;, so free defaults and DLC costumes (not in the
    /// contract) resolve to no location and the send safely no-ops.
    /// </summary>
    [HarmonyPatch(typeof(ConfidencePointManager), "UnlockCostume")]
    public static class Patch_UnlockCostume
    {
        [HarmonyPostfix]
        public static void Postfix(CharacterNames _name, string _costume, bool __result)
        {
            if (!ApManager.IsActive || !__result) return;
            if (ApManager.OutfitRestoreInProgress) return;   // re-applying a server-recorded purchase, not a new check
            try { ApManager.ReportOutfitPurchase(_name.ToString(), _costume); }
            catch (System.Exception e) { MainMod.Logger.LogWarning($"[AP] UnlockCostume hook error: {e.Message}"); }
        }
    }

    /// <summary>
    /// Sends a confidence-goal location check only when a goal is NEWLY completed this run.
    ///
    /// ConfidenceGoal.IsObjectiveComplete() just returns the persisted `irreversiblyCompleted`
    /// flag, and PreviouslyCompleted() is true once the goal has been recorded. So we mirror the
    /// game's own award condition (complete AND not previously recorded) in a PREFIX — before
    /// ProcessGoal records it. This fixes the false-positive where replaying a level on a save
    /// that already completed the goal would re-send it.
    /// </summary>
    [HarmonyPatch(typeof(ConfidencePointManager), "ProcessGoal")]
    public static class Patch_ProcessGoal
    {
        [HarmonyPrefix]
        public static void Prefix(ConfidenceGoal _goal)
        {
            if (!ApManager.IsActive || _goal == null) return;
            try
            {
                if (!_goal.IsObjectiveComplete()) return;   // not achieved
                if (_goal.PreviouslyCompleted()) return;    // already recorded (replay / prior save) -> don't re-send
                if (Managers.Level == null) return;

                string level = Managers.Level.currentFile.ShortName;   // "Folder/File.lvl"
                ApManager.ReportConfidenceGoal(level, _goal.SaveName, 1);
            }
            catch (System.Exception e)
            {
                MainMod.Logger.LogWarning($"[AP] ProcessGoal hook error: {e.Message}");
            }
        }
    }
}
