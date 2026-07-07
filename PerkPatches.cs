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
}
