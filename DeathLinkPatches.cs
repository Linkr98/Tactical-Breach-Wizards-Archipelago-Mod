using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Wizards.People;

namespace Tactical_Breach_Wizards_Archipelago_Mod
{
    /// <summary>
    /// The custom "Deathlink" status effect. Modeled on PoisonedCondition: it is a
    /// ForseeableCondition, so the danger preview forecasts the (lethal) damage and the game
    /// guarantees a foresee phase whenever someone bears it (GetPredictedActionCount counts
    /// foreseeable-condition holders). Its effect fires at the end of the foresee phase --
    /// after every predicted enemy action, i.e. the last thing in the turn order -- and kills
    /// the owner outright. Nothing in the player's toolkit removes it; the only escape is
    /// completing the room's objectives that same turn (the same guard poison uses), which
    /// reads as the wizard extracting in time.
    /// </summary>
    public class ApDeathlinkCondition : Condition, ForseeableCondition
    {
        public const string ConditionSaveName = "ApDeathlink";

        // Forecast exactly lethal damage so the preview UI shows the death.
        public int forseenDamage => (owner != null && owner.currentHealth > 0) ? owner.currentHealth : 1;

        public override void OnAdd()
        {
            base.OnAdd();
            if (owner != null) Managers.Events.Log(owner.displayName, "is", displayName);
        }

        public void OnEndOfForeseePhase()
        {
            if (Managers.Level.ObjectivesComplete) return;   // extracted in time (poison parity)
            if (owner == null || !owner.Alive()) return;      // someone else got them first
            DeathLinkPatches.MarkKilledByDeathlink(owner);    // don't echo this death back out
            owner.GetKilled();                                // clears this condition via OnKilled
            Managers.Turn.UnitIsActingFor(0.3f);
        }
    }

    /// <summary>
    /// DeathLink wiring. OUTGOING: a wizard who was alive when the player turn started but is
    /// dead when the turn is committed (or when the level fails, or -- for enemy-phase deaths --
    /// when the next turn begins) sends a DeathLink. INCOMING: each received death marks one
    /// random living wizard in the current mission with <see cref="ApDeathlinkCondition"/> at the
    /// start of the next wizard turn. Applying it in the StartOfWizardTurn PREFIX means it is
    /// baked into that turn's save snapshot, so reset-turn/rewind cannot shake it off.
    /// All hooks no-op unless AP is connected and the slot has death_link enabled.
    /// </summary>
    public static class DeathLinkPatches
    {
        // Living real wizards when the current player turn began.
        private static readonly HashSet<Wizard> _aliveAtTurnStart = new HashSet<Wizard>();
        // Deaths already sent this turn (a commit-reported death must not re-send next turn).
        private static readonly HashSet<Person> _reported = new HashSet<Person>();
        // Deaths caused by an incoming deathlink -- excluded from outgoing to prevent ping-pong.
        private static readonly HashSet<Person> _killedByDeathlink = new HashSet<Person>();

        private static ApDeathlinkCondition _master;  // reused across scene reloads

        private static bool DeathlinkOn()
            => ApManager.IsActive && ApManager.State != null && ApManager.State.DeathLink;

        public static void MarkKilledByDeathlink(Person person) => _killedByDeathlink.Add(person);

        public static void OnLevelStart()
        {
            _aliveAtTurnStart.Clear();
            _reported.Clear();
            _killedByDeathlink.Clear();
        }

        /// <summary>Register the Deathlink condition master with the scene's StatusItemManager so
        /// the normal AddCondition/CreateCondition/save-load machinery can resolve it by name.
        /// The registry and StatusData.saveName are private, hence the reflection. The icon must
        /// be a sprite that already exists in the game's TMP sprite atlas, so one is borrowed
        /// from an existing condition. Registered unconditionally (harmless offline) so a turn
        /// snapshot containing the condition always loads cleanly.</summary>
        public static void RegisterConditionMaster(StatusItemManager mgr)
        {
            var map = (Dictionary<string, StatusData>)AccessTools.Field(typeof(StatusItemManager), "statusDataMap").GetValue(mgr);
            var names = (HashSet<string>)AccessTools.Field(typeof(StatusItemManager), "conditionNames").GetValue(mgr);
            if (map == null || names == null || map.ContainsKey(ApDeathlinkCondition.ConditionSaveName)) return;

            if (_master == null)
            {
                _master = ScriptableObject.CreateInstance<ApDeathlinkCondition>();
                _master.name = "ApDeathlink";
                // Keep the runtime-created master alive across scene loads and safe from
                // Resources.UnloadUnusedAssets (DontDestroyOnLoad only covers GameObjects).
                _master.hideFlags = HideFlags.HideAndDontSave;
                AccessTools.Field(typeof(StatusData), "saveName").SetValue(_master, ApDeathlinkCondition.ConditionSaveName);
                _master.displayName = "Deathlink";
                _master.description = "A death in another world reaches for this wizard: they die at the end of this turn, " +
                                      "after everything else acts. Complete the room's objectives first to escape it.";
                _master.showInHealthUI = true;
                _master.showCount = false;
                _master.permanent = false;
                _master.preventConditions = new List<Condition>();  // iterated by AddCondition; must not be null
                if (ApIcon.Ready)
                {
                    // The Archipelago logo, loaded from ap_icon.png and registered with TMP so
                    // sprite tags resolve it. colour tints BOTH render paths, so keep it white
                    // to preserve the logo's own colors.
                    _master.icon = ApIcon.Sprite;
                    _master.colour = Color.white;
                }
                else
                {
                    // Fallback: borrow a stock condition icon (must already exist in the TMP
                    // sprite atlas) and tint it deathlink-red.
                    _master.colour = new Color(0.85f, 0.15f, 0.15f);
                    StatusData donor = map.TryGetValue("Poisoned", out var poisoned)
                        ? poisoned
                        : map.Values.FirstOrDefault(sd => sd.icon != null);
                    if (donor != null) _master.icon = donor.icon;
                }
            }
            map[ApDeathlinkCondition.ConditionSaveName] = _master;
            names.Add(ApDeathlinkCondition.ConditionSaveName);
            MainMod.Logger.LogInfo("[AP] Deathlink condition registered with StatusItemManager.");
        }

        /// <summary>Start of a player turn: first report anyone who died during the enemy phase
        /// (still against last turn's snapshot), then re-snapshot the living, then hand any
        /// pending incoming deathlinks to a random wizard -- before the game saves the turn
        /// state, so the mark survives reset/rewind.</summary>
        public static void OnStartOfWizardTurn()
        {
            if (!DeathlinkOn()) return;
            ReportNewDeaths();

            _reported.Clear();
            _killedByDeathlink.Clear();
            _aliveAtTurnStart.Clear();
            foreach (Wizard w in Lists.RealWizards)
                if (w != null && w.gameObject.activeInHierarchy && w.Alive())
                    _aliveAtTurnStart.Add(w);

            ApplyPendingDeathlinks();
        }

        public static void OnCommitTurn()
        {
            if (DeathlinkOn()) ReportNewDeaths();
        }

        public static void OnLevelFailed()
        {
            if (DeathlinkOn()) ReportNewDeaths();
        }

        /// <summary>Send one DeathLink covering every wizard who was alive at the turn snapshot
        /// and is dead now. Skips blinked-out/extracted wizards (inactive), pending
        /// resurrections (Banks can bring them back), already-reported deaths, and wizards our
        /// own deathlink condition killed.</summary>
        private static void ReportNewDeaths()
        {
            var dead = new List<string>();
            foreach (Wizard w in _aliveAtTurnStart)
            {
                if (w == null || !w.gameObject.activeInHierarchy) continue;
                if (w.Alive() || w.PendingResurrection) continue;
                if (_reported.Contains(w) || _killedByDeathlink.Contains(w)) continue;
                _reported.Add(w);
                dead.Add(w.displayName);
            }
            if (dead.Count == 0) return;

            string where;
            try { where = Managers.Level.currentFile.ShortName; }
            catch { where = "the field"; }
            ApManager.SendDeathLink($"{string.Join(" and ", dead.ToArray())} went down in {where}");
        }

        private static void ApplyPendingDeathlinks()
        {
            while (ApManager.PendingDeathCount > 0)
            {
                List<Wizard> candidates = Lists.RealWizards
                    .Where(w => w != null && w.gameObject.activeInHierarchy && w.Alive())
                    .ToList();
                if (candidates.Count == 0) return;  // no valid target yet; stays pending

                // Prefer a wizard not already marked; stack (harmlessly) only if all are.
                List<Wizard> unmarked = candidates
                    .Where(w => !w.conditions.HasCondition(ApDeathlinkCondition.ConditionSaveName))
                    .ToList();
                List<Wizard> pool = unmarked.Count > 0 ? unmarked : candidates;
                Wizard target = pool[UnityEngine.Random.Range(0, pool.Count)];

                if (!ApManager.TryDequeuePendingDeath(out string cause)) return;
                Condition added = target.conditions.AddCondition(ApDeathlinkCondition.ConditionSaveName, 1);
                if (added == null)
                {
                    MainMod.Logger.LogWarning($"[AP] Failed to apply Deathlink condition to {target.displayName}; death dropped.");
                    continue;
                }
                string suffix = string.IsNullOrEmpty(cause) ? "" : $"  ({cause})";
                ApManager.ToastDeath($"DeathLink: {target.displayName} dies at the end of this turn{suffix}");
                MainMod.Logger.LogInfo($"[AP] Deathlink condition applied to {target.displayName}.");
            }
        }
    }

    [HarmonyPatch(typeof(StatusItemManager), "Awake")]
    public static class Patch_RegisterDeathlinkCondition
    {
        [HarmonyPostfix]
        public static void Postfix(StatusItemManager __instance)
        {
            try { DeathLinkPatches.RegisterConditionMaster(__instance); }
            catch (Exception e) { MainMod.Logger.LogWarning($"[AP] Deathlink condition registration failed: {e}"); }
        }
    }

    [HarmonyPatch(typeof(TurnManager), "StartOfLevel")]
    public static class Patch_DeathLink_StartOfLevel
    {
        [HarmonyPostfix]
        public static void Postfix() => DeathLinkPatches.OnLevelStart();
    }

    // Runs every tick while the turn manager sits in this state (e.g. through dialogue), so the
    // handler is idempotent: reporting dedups per wizard, re-snapshotting the same living set is
    // a no-op, and the pending-death queue only drains once.
    [HarmonyPatch(typeof(TurnManager), "StartOfWizardTurn")]
    public static class Patch_DeathLink_StartOfWizardTurn
    {
        [HarmonyPrefix]
        public static void Prefix()
        {
            try { DeathLinkPatches.OnStartOfWizardTurn(); }
            catch (Exception e) { MainMod.Logger.LogWarning($"[AP] DeathLink turn-start hook failed: {e}"); }
        }
    }

    // The commit: the player pressed End Turn out of the foresee phase (or had nothing to
    // foresee). Wizards dead here were committed dead -- rewinding is no longer free.
    [HarmonyPatch(typeof(TurnManager), "EndPredictedActions")]
    public static class Patch_DeathLink_CommitTurn
    {
        [HarmonyPrefix]
        public static void Prefix()
        {
            try { DeathLinkPatches.OnCommitTurn(); }
            catch (Exception e) { MainMod.Logger.LogWarning($"[AP] DeathLink commit hook failed: {e}"); }
        }
    }

    // Level failed mid enemy turn (e.g. the whole squad went down): the next StartOfWizardTurn
    // never comes, so report here.
    [HarmonyPatch(typeof(TurnManager), "SetGameOver")]
    public static class Patch_DeathLink_LevelFailed
    {
        [HarmonyPrefix]
        public static void Prefix()
        {
            try { DeathLinkPatches.OnLevelFailed(); }
            catch (Exception e) { MainMod.Logger.LogWarning($"[AP] DeathLink game-over hook failed: {e}"); }
        }
    }
}
