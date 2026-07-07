using HarmonyLib;
using Wizards.Analytics;

namespace Tactical_Breach_Wizards_Archipelago_Mod
{
    /// <summary>
    /// Universal mission-complete hook. AnalyticsManager.LevelCompleteEvent fires only on a
    /// successful level completion (campaign AND flashback/replay). We send the mission-complete
    /// check when the just-finished level is the mission's final level. This covers the hub's
    /// flashback launches, where CompleteStageInternal does NOT run.
    /// </summary>
    [HarmonyPatch(typeof(AnalyticsManager), nameof(AnalyticsManager.LevelCompleteEvent))]
    public static class Patch_LevelCompleteEvent
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            if (!ApManager.IsActive) return;
            try
            {
                if (Managers.Level == null) return;
                var cur = Managers.Level.currentFile;
                if (!ApLookup.MissionByFolder.TryGetValue(cur.folder, out var mission)) return;
                // Halfway checkpoint: an easier/earlier check that fires when the mission's
                // ceil(N/2) room finishes. HalfLevel is null for single-room missions (no check).
                if (mission.HalfLevel != null && cur.FileName == mission.HalfLevel)
                    ApManager.ReportMissionHalf(mission.StageID);
                if (mission.LastLevel != null && cur.FileName == mission.LastLevel)
                    ApManager.ReportMissionComplete(mission.StageID);
            }
            catch (System.Exception e)
            {
                MainMod.Logger.LogWarning($"[AP] LevelCompleteEvent hook error: {e.Message}");
            }
        }
    }
}
