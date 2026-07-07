using HarmonyLib;
using Wizards.SaveSystem;

namespace Tactical_Breach_Wizards_Archipelago_Mod
{
    // Re-syncs AP-granted state after ANY save-data (re)load. The game reloads progress from
    // disk far more often than it saves: LevelManager.UnlockProgress calls LoadProgressData at
    // EVERY level start and level switch (and RestartCurrentLevel on restart), and the perk /
    // outfit rewind buttons reload their managers — all of which used to wipe in-memory AP
    // grants (perk points, confidence, outfits, abilities) that had never been written to disk.
    // AP saves are also routinely reset per session (no story progression -> "new game" each
    // time), so the server is the source of truth; reconciling after every load keeps the game
    // converged on it. SyncAll is idempotent and cheap, so over-calling is harmless. Gated on
    // GameStarted so menu-time loads (boot, save-slot browsing, continue's pre-load) stay
    // untouched — OnGameStarted runs the first sync for those flows.

    [HarmonyPatch(typeof(SaveManager), "LoadProgressData")]
    public static class Patch_Resync_LoadProgressData
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            if (ApManager.IsActive && ApManager.GameStarted) ApManager.SyncAll();
        }
    }

    [HarmonyPatch(typeof(SaveManager), "LoadPerksData")]
    public static class Patch_Resync_LoadPerksData
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            if (ApManager.IsActive && ApManager.GameStarted) ApManager.SyncAll();
        }
    }

    [HarmonyPatch(typeof(SaveManager), "LoadOutfitData")]
    public static class Patch_Resync_LoadOutfitData
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            if (ApManager.IsActive && ApManager.GameStarted) ApManager.SyncAll();
        }
    }
}
