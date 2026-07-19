using HarmonyLib;
using Wizards.Perks;

namespace Tactical_Breach_Wizards_Archipelago_Mod
{
    /// <summary>
    /// Blocks natural perk-point earning while connected to AP, so perk points come ONLY from AP
    /// perk_point items (granted via <see cref="ApManager.SyncPerks"/>). PerkManager.AddPerkPoints is
    /// the single grant chokepoint -- the vanilla level-up path (AddPerkPointsToCharacters, run when
    /// you reach a Perks stage / progress a character) tops characters up through it. We let our own
    /// grant through with a gate flag and allow respec refunds (_isRefund), and block everything else.
    /// </summary>
    [HarmonyPatch(typeof(PerkManager), "AddPerkPoints")]
    public static class Patch_AddPerkPoints
    {
        public static bool ApGrantInProgress;

        [HarmonyPrefix]
        public static bool Prefix(int _points, bool _isRefund)
        {
            if (!ApManager.IsActive) return true;   // vanilla untouched offline
            if (ApGrantInProgress) return true;     // our own AP grant -> allow
            if (_isRefund) return true;             // respec refund -> allow
            if (_points <= 0) return true;          // non-positive pass through
            return false;                           // block all natural earning
        }
    }

    /// <summary>
    /// Makes AP the ONLY source of the ability perks it controls. The vanilla campaign auto-grants
    /// base abilities (and the specials) by calling PerkManager.AcquirePerk from each level's
    /// `grantPerk[]` (LevelManager.grantPerksAtStart) when you play it -- which would leak abilities
    /// in even though they're AP items now. We block AcquirePerk for any AP-controlled ability perk
    /// unless it's our own grant (gate flag). Perk-tree purchases and all other perks are untouched.
    /// </summary>
    [HarmonyPatch(typeof(PerkManager), "AcquirePerk")]
    public static class Patch_AcquirePerk
    {
        public static bool ApGrantInProgress;

        [HarmonyPrefix]
        public static bool Prefix(CharacterPerk _perk)
        {
            if (!ApManager.IsActive || _perk == null) return true;
            if (ApGrantInProgress) return true;                          // our own AP grant -> allow
            if (ApManager.IsApControlledAbilityPerk(_perk.SaveName))
                return false;                                            // vanilla grantPerk[] -> block
            return true;
        }
    }

    /// <summary>
    /// Unlocks perk respec from the start while connected to AP. Vanilla allows unlimited free
    /// perk refunds only after the campaign is complete (CanPayForPerkRefund falls back to
    /// Managers.Stage.LastStageCompleted when no banked respec points remain); in AP the campaign
    /// is never "complete" in the game's eyes, and perk points arrive as items the player should
    /// be free to re-plan around. Forcing the pay-check on gives exactly the post-game behavior;
    /// everything else still applies (full perks screen only, purchased tree perks only, and a
    /// perk that other acquired perks depend on can't be refunded first).
    /// </summary>
    [HarmonyPatch(typeof(PerkPurchaseButton), "CanPayForPerkRefund", MethodType.Getter)]
    public static class Patch_FreePerkRespec
    {
        [HarmonyPostfix]
        public static void Postfix(ref bool __result)
        {
            if (ApManager.IsActive) __result = true;
        }
    }

    /// <summary>
    /// Freezes the banked respec-point counter while refunds are unlimited. The refund flow
    /// debits it (going negative once forced refunds bypass the balance) and vanilla level-up
    /// beats credit it -- either would leave a meaningless "N Perk Refunds available" banner in
    /// the perks screen, which reads as a limit that no longer exists.
    /// </summary>
    [HarmonyPatch(typeof(PerkManager), "AddRespecPoints")]
    public static class Patch_FreezeRespecPoints
    {
        [HarmonyPrefix]
        public static bool Prefix() => !ApManager.IsActive;
    }
}
