using System.Collections.Generic;

namespace Tactical_Breach_Wizards_Archipelago_Mod
{
    /// <summary>
    /// The Archipelago-authoritative progression state, rebuilt from the items the server
    /// has granted this slot. Patches read this instead of the game's own unlock state.
    /// All mutation happens on the Unity main thread (see <see cref="ApManager"/>).
    /// </summary>
    public class ApState
    {
        // Internal CharacterNames strings (e.g. "NavySeer").
        public readonly HashSet<string> UnlockedCharacters = new HashSet<string>();
        // Mission stageIDs (e.g. "Game_Prologue").
        public readonly HashSet<string> UnlockedMissions = new HashSet<string>();
        // Special-ability stageIDs (e.g. "Unlock_DeathsFloor").
        public readonly HashSet<string> UnlockedAbilities = new HashSet<string>();
        // Owned outfit costume keys "Character:saveName".
        public readonly HashSet<string> OwnedOutfits = new HashSet<string>();
        // Perk points granted per character (capped by the apworld at each tree's real size).
        public readonly Dictionary<string, int> PerkPoints = new Dictionary<string, int>();
        // Confidence-boost items received per character (internal CharacterNames -> count).
        // Once the mod blocks natural confidence earning, these are the ONLY confidence source;
        // each is worth ConfidencePerBoost points for that character.
        public readonly Dictionary<string, int> ConfidenceItems = new Dictionary<string, int>();
        // Generic junk filler ("Donut") received count. No in-game effect; kept for stats/UI.
        public int FillerItems;

        // From slot_data.
        public bool DeathLink;
        public int ConfidencePerBoost = 5;   // confidence granted per Confidence Boost item
        public string StartCharacter;   // internal CharacterNames
        public string StartMission;     // stageID

        /// <summary>Apply one received AP item (by id) to the derived state. Returns the
        /// item key applied, or null if the id is unknown.</summary>
        public string ApplyItem(long itemId)
        {
            if (!ApLookup.TryGetItem(itemId, out var def))
                return null;

            string payload = ApLookup.AfterColon(def.Key);
            switch (def.Category)
            {
                case "character":
                    UnlockedCharacters.Add(payload);
                    break;
                case "mission_access":
                    UnlockedMissions.Add(payload);
                    break;
                case "ability":
                    UnlockedAbilities.Add(payload);
                    break;
                case "outfit":
                    OwnedOutfits.Add(payload);   // "Character:saveName"
                    break;
                case "perk_point":
                    PerkPoints[payload] = (PerkPoints.TryGetValue(payload, out var n) ? n : 0) + 1;
                    break;
                case "confidence":
                    ConfidenceItems[payload] = (ConfidenceItems.TryGetValue(payload, out var c) ? c : 0) + 1;
                    break;
                case "filler":
                    FillerItems++;
                    break;
            }
            return def.Key;
        }

        public bool IsCharacterUnlocked(string internalName) => UnlockedCharacters.Contains(internalName);
        public bool IsMissionUnlocked(string stageID) => UnlockedMissions.Contains(stageID);
        public bool IsAbilityUnlocked(string stageID) => UnlockedAbilities.Contains(stageID);
        public bool IsOutfitOwned(string character, string saveName) => OwnedOutfits.Contains(character + ":" + saveName);
        public int PerkPointsFor(string internalName) => PerkPoints.TryGetValue(internalName, out var n) ? n : 0;
        public int ConfidenceItemsFor(string internalName) => ConfidenceItems.TryGetValue(internalName, out var n) ? n : 0;
        /// <summary>Total confidence this slot should have EARNED for a character from AP items
        /// (item count * ConfidencePerBoost). In-game spending is separate.</summary>
        public int ConfidenceEarnedFor(string internalName) => ConfidenceItemsFor(internalName) * ConfidencePerBoost;
    }
}
