using System.Collections.Generic;

namespace Tactical_Breach_Wizards_Archipelago_Mod
{
    /// <summary>
    /// Indexes the generated <see cref="ApData"/> tables so patches can translate between
    /// AP numeric ids and the game's own identifiers (character names, mission stageIDs,
    /// confidence-goal keys, costume saveNames, ability stageIDs).
    ///
    /// Key formats (from tools/generate_ap_data.py):
    ///   item   char:NavySeer | missionaccess:&lt;stageID&gt; | ability:&lt;stageID&gt; |
    ///          perkpoint:NavySeer | outfit:&lt;Char&gt;:&lt;saveName&gt; | confidence:&lt;Char&gt; | filler:donut
    ///   loc    mission:&lt;stageID&gt; | goal:&lt;level&gt;|&lt;goalName&gt;|&lt;ordinal&gt; |
    ///          buyoutfit:&lt;Char&gt;:&lt;saveName&gt; | abilityloc:&lt;stageID&gt; | recruit:&lt;Char&gt;
    /// </summary>
    public static class ApLookup
    {
        public static readonly Dictionary<long, ApData.ItemDef> ItemById = new Dictionary<long, ApData.ItemDef>();
        public static readonly Dictionary<string, ApData.ItemDef> ItemByKey = new Dictionary<string, ApData.ItemDef>();
        public static readonly Dictionary<long, ApData.LocDef> LocById = new Dictionary<long, ApData.LocDef>();
        public static readonly Dictionary<string, ApData.LocDef> LocByKey = new Dictionary<string, ApData.LocDef>();

        /// <summary>The 5 internal CharacterNames the mod manages (derived from character items).</summary>
        public static readonly HashSet<string> PlayableCharacters = new HashSet<string>();

        public static readonly Dictionary<string, ApData.MissionDef> MissionByFolder = new Dictionary<string, ApData.MissionDef>();
        public static readonly Dictionary<string, ApData.MissionDef> MissionByStageId = new Dictionary<string, ApData.MissionDef>();

        static ApLookup()
        {
            foreach (var it in ApData.Items)
            {
                ItemById[it.Id] = it;
                ItemByKey[it.Key] = it;
                if (it.Category == "character")
                    PlayableCharacters.Add(AfterColon(it.Key));
            }
            foreach (var m in ApData.Missions)
            {
                MissionByFolder[m.Folder] = m;
                MissionByStageId[m.StageID] = m;
            }
            foreach (var lo in ApData.Locations)
            {
                LocById[lo.Id] = lo;
                LocByKey[lo.Key] = lo;
            }
        }

        // ----- item id helpers -----
        public static bool TryGetItem(long id, out ApData.ItemDef def) => ItemById.TryGetValue(id, out def);

        /// <summary>For an item key, returns its category, or null if unknown.</summary>
        public static string ItemCategory(long id) => ItemById.TryGetValue(id, out var d) ? d.Category : null;

        // ----- location id helpers (for sending checks from game events) -----
        public static long? LocId(string key) => LocByKey.TryGetValue(key, out var d) ? d.Id : (long?)null;

        public static long? MissionCompleteId(string stageID) => LocId("mission:" + stageID);
        public static long? MissionHalfId(string stageID) => LocId("missionhalf:" + stageID);
        public static long? AbilityUnlockId(string stageID) => LocId("abilityloc:" + stageID);
        public static long? RecruitId(string characterInternal) => LocId("recruit:" + characterInternal);
        public static long? OutfitPurchaseId(string characterInternal, string saveName)
            => LocId("buyoutfit:" + characterInternal + ":" + saveName);
        public static long? ConfidenceGoalId(string level, string goalName, int ordinal)
            => LocId("goal:" + level + "|" + goalName + "|" + ordinal);

        /// <summary>The victory location (Counterheist: The Roof).</summary>
        public const string GoalLocationKey = "mission:Game_Finale_Roof";
        public static long? GoalLocationId() => LocId(GoalLocationKey);

        // ----- key parsers -----
        /// <summary>Extracts the part after the first ':' in a key (e.g. char:NavySeer -> NavySeer).</summary>
        public static string AfterColon(string key)
        {
            int i = key.IndexOf(':');
            return i < 0 ? key : key.Substring(i + 1);
        }
    }
}
