using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Wizards.People;
using Wizards.SaveSystem;
using Wizards.Stages;

namespace Tactical_Breach_Wizards_Archipelago_Mod
{
    /// <summary>
    /// Phase 1: read-only enumeration of the game's progression data.
    /// Writes a single JSON file that becomes the shared source of truth for both
    /// the C# mod and the Python .apworld (mission list + ordering, confidence
    /// goals, characters, missions, costumes/outfits, features, perk recipients).
    /// Nothing here mutates game state.
    /// </summary>
    public static class DataDumper
    {
        private static void Log(string msg) => MainMod.Logger?.LogInfo("[Dump] " + msg);
        private static void Warn(string msg) => MainMod.Logger?.LogWarning("[Dump] " + msg);

        public static void DumpAll(string trigger)
        {
            try
            {
                Log($"Starting AP data dump (trigger: {trigger})...");

                var root = new Dictionary<string, object>
                {
                    ["_about"] = "Tactical Breach Wizards Archipelago data dump. Source of truth for item/location IDs.",
                    ["modVersion"] = MainMod.PluginVersion,
                    ["acts"] = DumpStages(),
                    ["confidenceGoals"] = DumpConfidenceGoals(),
                    ["unlockableCharacters"] = DumpCharacters(),
                    ["unlockableMissions"] = DumpMissions(),
                    ["unlockableCostumes"] = DumpCostumes(),
                    ["unlockableFeatures"] = DumpFeatures(),
                    ["costumesByCharacter"] = DumpCostumesByCharacter(),
                };

                string path = Path.Combine(PluginDir(), "tbw_ap_dump.json");
                File.WriteAllText(path, JsonConvert.SerializeObject(root, Formatting.Indented));
                Log($"Wrote dump to: {path}");
            }
            catch (Exception e)
            {
                Warn($"Dump failed: {e}");
            }
        }

        private static string PluginDir()
        {
            return Path.GetDirectoryName(typeof(DataDumper).Assembly.Location);
        }

        // --- Campaign stage list (canonical ordering, grouped by Act) ---
        private static object DumpStages()
        {
            var acts = new List<object>();
            try
            {
                StageManager sm = Managers.Stage;
                if (sm == null || sm.stageList == null)
                {
                    Warn("Managers.Stage / stageList not ready.");
                    return acts;
                }

                var actList = sm.stageList.actList;
                for (int a = 0; a < actList.Count; a++)
                {
                    var stages = new List<object>();
                    foreach (LoadableStage stage in actList[a].stages)
                    {
                        if (stage == null) continue;
                        stages.Add(DumpStage(stage));
                    }
                    acts.Add(new Dictionary<string, object>
                    {
                        ["actIndex"] = a,
                        ["stages"] = stages,
                    });
                }
            }
            catch (Exception e) { Warn("DumpStages: " + e); }
            return acts;
        }

        private static object DumpStage(LoadableStage stage)
        {
            var d = new Dictionary<string, object>();
            TryAdd(d, "stageID", () => stage.stageID);
            TryAdd(d, "displayName", () => stage.displayName);
            TryAdd(d, "type", () => stage.Type.ToString());
            TryAdd(d, "stageData", () => stage.StageData);
            TryAdd(d, "endOfCampaign", () => stage.EndOfCampaign);
            TryAdd(d, "characterUnlocks", () => EnumNames(stage.GetCharacterUnlocks()));
            TryAdd(d, "perkPointRecipients", () => EnumNames(stage.GetPerkPointRecipients()));
            TryAdd(d, "requiredPerkRecipients", () => EnumNames(stage.GetRequiredPerkRecipients()));
            TryAdd(d, "buildingFolder", () =>
            {
                LevelFile f = stage.GetStageBuildingFolder();
                return f != null ? f.folder : null;
            });
            return d;
        }

        // --- Confidence goals (the bulk of the location checks) ---
        private static object DumpConfidenceGoals()
        {
            var goals = new List<object>();
            try
            {
                StageManager sm = Managers.Stage;
                if (sm == null)
                {
                    Warn("Managers.Stage not ready for confidence goals.");
                    return goals;
                }

                sm.LoadAllConfidenceGoals();
                var all = sm.allGoals;
                if (all == null)
                {
                    Warn("allGoals null after LoadAllConfidenceGoals().");
                    return goals;
                }

                foreach (var g in all)
                {
                    var d = new Dictionary<string, object>();
                    TryAdd(d, "level", () => g.level.ShortName);
                    TryAdd(d, "levelFolder", () => g.level.folder);
                    TryAdd(d, "goalName", () => g.goalName);
                    TryAdd(d, "character", () => g.character.ToString());
                    TryAdd(d, "isFinaleGoal", () => g.isFinaleGoal);
                    goals.Add(d);
                }
                Log($"Confidence goals: {goals.Count}");
            }
            catch (Exception e) { Warn("DumpConfidenceGoals: " + e); }
            return goals;
        }

        // --- Unlockables (items) ---
        private static object DumpCharacters()
        {
            var list = new List<object>();
            try
            {
                ProgressManager pm = Managers.Progress;
                if (pm == null) { Warn("Managers.Progress not ready (characters)."); return list; }
                foreach (UnlockableCharacter c in pm.AllUnlockableCharacters)
                {
                    var d = new Dictionary<string, object>();
                    TryAdd(d, "characterName", () => c.characterName.ToString());
                    TryAdd(d, "saveName", () => c.saveName);
                    TryAdd(d, "displayName", () => c.displayName);
                    list.Add(d);
                }
                Log($"Unlockable characters: {list.Count}");
            }
            catch (Exception e) { Warn("DumpCharacters: " + e); }
            return list;
        }

        private static object DumpMissions()
        {
            var list = new List<object>();
            try
            {
                ProgressManager pm = Managers.Progress;
                if (pm == null) { Warn("Managers.Progress not ready (missions)."); return list; }
                foreach (UnlockableMission m in pm.AllUnlockableMissions)
                {
                    var d = new Dictionary<string, object>();
                    TryAdd(d, "name", () => m.name);
                    TryAdd(d, "saveName", () => m.saveName);
                    TryAdd(d, "displayName", () => m.displayName);
                    TryAdd(d, "missionFolders", () => m.missionFolders != null ? new List<string>(m.missionFolders) : null);
                    list.Add(d);
                }
                Log($"Unlockable missions: {list.Count}");
            }
            catch (Exception e) { Warn("DumpMissions: " + e); }
            return list;
        }

        private static object DumpCostumes()
        {
            var list = new List<object>();
            try
            {
                ProgressManager pm = Managers.Progress;
                if (pm == null) { Warn("Managers.Progress not ready (costumes)."); return list; }
                foreach (UnlockableCostume c in pm.AllUnlockableCostumes)
                {
                    var d = new Dictionary<string, object>();
                    TryAdd(d, "saveName", () => c.saveName);
                    TryAdd(d, "displayName", () => c.displayName);
                    list.Add(d);
                }
                Log($"Unlockable costumes/outfits: {list.Count}");
            }
            catch (Exception e) { Warn("DumpCostumes: " + e); }
            return list;
        }

        private static object DumpFeatures()
        {
            var list = new List<object>();
            try
            {
                ProgressManager pm = Managers.Progress;
                if (pm == null) { Warn("Managers.Progress not ready (features)."); return list; }
                foreach (Unlockable u in pm.AllUnlockables.OfType<UnlockableFeature>())
                {
                    var d = new Dictionary<string, object>();
                    TryAdd(d, "saveName", () => u.saveName);
                    TryAdd(d, "displayName", () => u.displayName);
                    list.Add(d);
                }
                Log($"Unlockable features: {list.Count}");
            }
            catch (Exception e) { Warn("DumpFeatures: " + e); }
            return list;
        }

        // --- Costumes/outfits (live on each character's art prefab, not in ProgressManager) ---
        private static object DumpCostumesByCharacter()
        {
            var result = new List<object>();
            try
            {
                CharacterManager cm = Managers.Characters;
                if (cm == null) { Warn("Managers.Characters not ready (costumes)."); return result; }

                int total = 0;
                foreach (CharacterNames name in CharacterManager.PlayableCharacterList)
                {
                    var costumes = new List<object>();
                    try
                    {
                        foreach (Costume c in cm.GetCharacterCostumes(name))
                        {
                            if (c == null) continue;
                            costumes.Add(new Dictionary<string, object>
                            {
                                ["saveName"] = c.saveName,
                                ["displayName"] = c.displayName,
                                ["unlockCost"] = c.unlockCost,   // confidence cost; <=0 = free default
                                ["special"] = c.special,         // true = DLC costume
                            });
                            total++;
                        }
                    }
                    catch (Exception e) { Warn($"costumes for {name}: {e.GetType().Name}"); }

                    result.Add(new Dictionary<string, object>
                    {
                        ["character"] = name.ToString(),
                        ["costumes"] = costumes,
                    });
                }
                Log($"Costumes across playable characters: {total}");
            }
            catch (Exception e) { Warn("DumpCostumesByCharacter: " + e); }
            return result;
        }

        // --- helpers ---
        private static List<string> EnumNames(IEnumerable<CharacterNames> names)
        {
            return names == null ? new List<string>() : names.Select(n => n.ToString()).ToList();
        }

        private static void TryAdd(Dictionary<string, object> d, string key, Func<object> getter)
        {
            try { d[key] = getter(); }
            catch (Exception e) { d[key] = null; Warn($"field '{key}': {e.GetType().Name}"); }
        }
    }
}
