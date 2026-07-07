using HarmonyLib;
using Wizards.People;
using Wizards.SaveSystem;

namespace Tactical_Breach_Wizards_Archipelago_Mod
{
    /// <summary>
    /// First vertical slice of "AP items are authoritative": a wizard is unlocked iff the
    /// AP server has granted that character item. Only active while connected, and only for
    /// the 5 playable characters — everything else (and offline play) falls through to vanilla.
    /// </summary>
    [HarmonyPatch(typeof(ProgressManager), nameof(ProgressManager.IsCharacterUnlocked))]
    public static class Patch_IsCharacterUnlocked
    {
        [HarmonyPrefix]
        public static bool Prefix(CharacterNames _name, ref bool __result)
        {
            if (!ApManager.IsActive) return true;            // offline -> vanilla

            string internalName = _name.ToString();
            if (!ApLookup.PlayableCharacters.Contains(internalName))
                return true;                                 // NPCs etc. -> vanilla

            __result = ApManager.State.IsCharacterUnlocked(internalName);
            return false;                                    // skip original
        }
    }
}
