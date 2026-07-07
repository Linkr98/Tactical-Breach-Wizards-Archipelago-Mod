using HarmonyLib;
using Wizards.Stages;
using Wizards.UI;

namespace Tactical_Breach_Wizards_Archipelago_Mod
{
    /// <summary>
    /// Sends a mission-complete location check whenever the game records a campaign stage as
    /// finished. CompleteStageInternal runs for every stage type; ReportMissionComplete only
    /// acts on the 29 campaign Gameplay stages (others map to no location) and fires the AP
    /// goal when the finale (Game_Finale_Roof) completes.
    /// </summary>
    [HarmonyPatch(typeof(StageManager), "CompleteStageInternal")]
    public static class Patch_CompleteStageInternal
    {
        [HarmonyPostfix]
        public static void Postfix(string _stageID)
        {
            if (!ApManager.IsActive) return;
            ApManager.ReportMissionComplete(_stageID);
        }
    }

    // NOTE: the New Game confirm hook lives in ApConnectUi.cs (Patch_GateNewGame): it gates the
    // confirm behind the AP connect dialog and, when connected, flags the mission-select redirect
    // consumed below. In AP mode the linear campaign is incompatible with out-of-order unlocks
    // (and the scripted early missions need abilities the player may not hold yet), so instead of
    // dropping into the first stage we route to the mission-select board. The swap happens in the
    // LoadNextStage patch (which the original confirm calls, via FadeThenLoad, after the fade), so
    // we don't duplicate the reset/save/difficulty setup or double-load.

    /// <summary>
    /// Redirects the New Game start's LoadNextStage to the mission-select board (chapter meta-menu,
    /// which opens in chapterSelect mode and shows AP-unlocked missions). Gated on the one-shot flag
    /// set by <see cref="Patch_GateNewGame"/> so normal stage progression is untouched. Runs after
    /// the original OnStartButtonClick reset, so grants applied here land on the fresh game.
    /// </summary>
    [HarmonyPatch(typeof(StageManager), "LoadNextStage")]
    public static class Patch_RedirectNewGameToMissionSelect
    {
        [HarmonyPrefix]
        public static bool Prefix(StageManager __instance)
        {
            if (!ApManager.IsActive || !ApManager.ConsumeNewGameRedirect()) return true;
            MainMod.Logger.LogInfo("[AP] New game -> routing to mission-select board instead of the linear first stage.");
            ApManager.OnGameStarted();  // now safe to apply items (reset has already run)
            __instance.LoadMetaMenuWithoutStage(MetaMenu.MetaMenuScene.Chapter);
            return false;               // skip loading the linear stage
        }
    }

    /// <summary>
    /// Continuing a save is also an active game, so enable item application (and flush anything
    /// received at the menu). Routing is left to the vanilla ContinueGame logic.
    /// </summary>
    [HarmonyPatch(typeof(StageManager), "ContinueGame")]
    public static class Patch_ContinueGame
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            if (!ApManager.IsActive) return;
            ApManager.OnGameStarted();
        }
    }
}
