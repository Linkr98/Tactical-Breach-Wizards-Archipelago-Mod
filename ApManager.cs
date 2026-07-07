using System;
using System.Collections.Generic;
using Archipelago.MultiClient.Net.Enums;
using HarmonyLib;
using UnityEngine;
using Wizards.LevelBuilding;
using Wizards.People;
using Wizards.Perks;
using Wizards.SaveSystem;

namespace Tactical_Breach_Wizards_Archipelago_Mod
{
    /// <summary>
    /// Main-thread owner of the AP connection. Persists across scenes, drains the inbound
    /// item/death queues each frame, keeps <see cref="ApState"/> up to date, polls keybinds,
    /// and gives the Harmony patches a single static surface for state + sending checks.
    /// </summary>
    public class ApManager : MonoBehaviour
    {
        public static ApManager Instance { get; private set; }

        private readonly ApClient _client = new ApClient();
        private readonly ApState _state = new ApState();
        private readonly HashSet<long> _sentChecks = new HashSet<long>();
        // Incoming DeathLinks waiting to be handed to a wizard (applied at the start of the next
        // wizard turn in a mission; see DeathLinkPatches). Main-thread only.
        private readonly Queue<string> _pendingDeaths = new Queue<string>();
        private bool _slotDataApplied;
        private long _frame;
        private bool _keyWarned;
        private bool _hubVisible;
        private Vector2 _hubScroll;
        private bool _gameStarted;             // true once a game is started/continued this session
        private bool _newGameRedirectPending;  // set by the New Game "Start" button; consumed in LoadNextStage

        public static ApState State => Instance?._state;
        public static bool IsActive => Instance != null && Instance._client.Connection == ApConnectionState.Connected;
        public static ApConnectionState Connection => Instance?._client.Connection ?? ApConnectionState.Disconnected;
        public static string LastError => Instance?._client.LastError;

        /// <summary>Drop the connection (dialog's Disconnect button). Received-item state is NOT
        /// cleared — it's cumulative per slot — so reconnecting to the SAME slot is safe, but
        /// joining a different multiworld needs a game restart.</summary>
        public static void Disconnect()
        {
            if (Instance == null) return;
            Instance._client.Disconnect();
            Instance._slotDataApplied = false;   // re-apply slot_data on the next connect
            MainMod.Logger.LogInfo("[AP] Disconnected. Reconnect to the same slot is safe; restart the game to join a different multiworld.");
        }

        /// <summary>Whether the player is in an active game (pressed New Game "Start" or Continue this
        /// session). Item grants/removals are deferred until this is true, so nothing is applied while
        /// sitting at the main menu -- received items are still tracked in <see cref="ApState"/> and
        /// flushed the moment a game starts.</summary>
        public static bool GameStarted => Instance != null && Instance._gameStarted;

        public static ApManager EnsureExists()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("TBW_ApManager");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<ApManager>();
            MainMod.Logger.LogInfo("[AP] ApManager GameObject created.");
            return Instance;
        }

        private void Awake() => MainMod.Logger.LogInfo("[AP] ApManager.Awake");
        private void OnEnable() => MainMod.Logger.LogInfo("[AP] ApManager.OnEnable");
        private void Start() => MainMod.Logger.LogInfo("[AP] ApManager.Start (MonoBehaviour is ticking)");

        public void Connect(string host, int port, string slot, string password)
        {
            MainMod.Logger.LogInfo($"[AP] Connecting to {host}:{port} as '{slot}'...");
            _client.Connect(host, port, slot, password);
        }

        private void Update()
        {
            _frame++;
            if (_frame == 1) MainMod.Logger.LogInfo("[AP] First Update tick — per-frame pump is alive.");

            // Apply slot_data once after a successful connect.
            if (!_slotDataApplied && _client.Connection == ApConnectionState.Connected && _client.SlotData != null)
            {
                ApplySlotData(_client.SlotData);
                _slotDataApplied = true;
                AddToast(new ApNotice(ApNoticeKind.Info, "Connected to Archipelago"));
            }

            // Drain displayable AP events (items sent/received/found) into the toast feed.
            while (_client.IncomingNotices.TryDequeue(out ApNotice notice))
            {
                AddToast(notice);
                MainMod.Logger.LogInfo($"[AP] {notice.Text}");
            }
            PruneToasts();

            // Drain received items on the main thread.
            bool applied = false;
            while (_client.IncomingItems.TryDequeue(out long itemId))
            {
                string key = _state.ApplyItem(itemId);
                applied = true;
                MainMod.Logger.LogInfo(key != null
                    ? $"[AP] Received item: {key} (id {itemId})"
                    : $"[AP] Received UNKNOWN item id {itemId}");
            }
            // Only push grants into the game once a game is underway; otherwise the items stay
            // recorded in ApState and get applied by OnGameStarted when the player starts/continues.
            if (applied)
            {
                if (_gameStarted) SyncAll();
                else MainMod.Logger.LogInfo("[AP] Item(s) received at menu — held until a game is started.");
            }

            while (_client.IncomingDeaths.TryDequeue(out string cause))
            {
                MainMod.Logger.LogInfo($"[AP] DeathLink received: {cause}");
                if (_state.DeathLink)
                {
                    _pendingDeaths.Enqueue(cause ?? "");
                    AddToast(new ApNotice(ApNoticeKind.Death,
                        (string.IsNullOrEmpty(cause) ? "DeathLink received" : $"DeathLink: {cause}")
                        + " — a wizard will be marked next turn"));
                }
                else
                {
                    AddToast(new ApNotice(ApNoticeKind.Death,
                        string.IsNullOrEmpty(cause) ? "DeathLink received!" : $"DeathLink: {cause}"));
                }
            }

            // Keybinds via the new Input System, isolated so a load failure can't kill the pump.
            try { PollKeys(); }
            catch (System.Exception e)
            {
                if (!_keyWarned) { _keyWarned = true; MainMod.Logger.LogWarning($"[AP] Keybinds unavailable: {e.GetType().Name} — use main-menu auto-connect."); }
            }
        }

        private void PollKeys()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return;
            if (kb.f8Key.wasPressedThisFrame) DataDumper.DumpAll("F8 keypress");
            if (kb.f9Key.wasPressedThisFrame) ApConnectDialog.ToggleStandalone();
            if (kb.f10Key.wasPressedThisFrame) _hubVisible = !_hubVisible;
        }

        // ----- item send/receive toast feed (top-center, Celeste-style banners) -----
        // Toasts show live AP traffic: items you receive (colored by AP's progression/useful/trap
        // classification), items your checks send to other players, your own items found locally,
        // DeathLinks, and connection status. Drawn every frame regardless of hub visibility; the
        // F10 hub keeps a longer "recent activity" scrollback of the same lines.
        private struct Toast { public string Text; public Color Color; public float Born; }
        private readonly List<Toast> _toasts = new List<Toast>();
        private readonly List<string> _noticeHistory = new List<string>();
        private const float ToastSeconds = 6f;   // lifetime of one banner
        private const float ToastFade = 1.2f;    // fade-out tail at the end of the lifetime
        private const int MaxToasts = 8;         // burst cap (e.g. a release) — oldest drop first
        private const int MaxHistory = 100;
        private GUIStyle _toastStyle;
        private Texture2D _toastBg;

        private void AddToast(ApNotice notice)
        {
            _toasts.Add(new Toast { Text = notice.Text, Color = NoticeColor(notice), Born = Time.realtimeSinceStartup });
            if (_toasts.Count > MaxToasts) _toasts.RemoveAt(0);
            _noticeHistory.Add(notice.Text);
            if (_noticeHistory.Count > MaxHistory) _noticeHistory.RemoveAt(0);
        }

        private void PruneToasts()
        {
            float now = Time.realtimeSinceStartup;
            _toasts.RemoveAll(t => now - t.Born > ToastSeconds);
        }

        // Text colors follow the Archipelago convention: plum = progression, blue = useful,
        // salmon = trap, gray = filler.
        private static Color NoticeColor(ApNotice n)
        {
            if (n.Kind == ApNoticeKind.Death) return new Color(1f, 0.35f, 0.35f);
            if (n.Kind == ApNoticeKind.Info) return new Color(0.65f, 0.9f, 1f);
            if ((n.Flags & ItemFlags.Trap) != 0) return new Color(1f, 0.55f, 0.45f);
            if ((n.Flags & ItemFlags.Advancement) != 0) return new Color(0.81f, 0.71f, 1f);
            if ((n.Flags & ItemFlags.NeverExclude) != 0) return new Color(0.55f, 0.75f, 1f);
            return new Color(0.85f, 0.85f, 0.85f);
        }

        private void DrawToasts()
        {
            if (_toasts.Count == 0) return;
            if (_toastStyle == null)
            {
                _toastBg = new Texture2D(1, 1);
                _toastBg.SetPixel(0, 0, new Color(0.05f, 0.05f, 0.08f, 0.88f));
                _toastBg.Apply();
                _toastStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 15,
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = true,
                    padding = new RectOffset(14, 14, 6, 6),
                };
                _toastStyle.normal.background = _toastBg;
            }

            float now = Time.realtimeSinceStartup;
            float width = Mathf.Min(640f, Screen.width - 40f);
            float x = (Screen.width - width) / 2f;
            float y = 12f;
            Color prevColor = GUI.color;
            foreach (var t in _toasts)
            {
                float age = now - t.Born;
                float alpha = Mathf.Clamp01((ToastSeconds - age) / ToastFade);
                var content = new GUIContent(t.Text);
                float h = _toastStyle.CalcHeight(content, width);
                _toastStyle.normal.textColor = t.Color;
                GUI.color = new Color(1f, 1f, 1f, alpha);   // fades text and backing together
                GUI.Label(new Rect(x, y, width, h), content, _toastStyle);
                y += h + 4f;
            }
            GUI.color = prevColor;
        }

        // ----- mission-select hub (MVP, toggle with F10) -----
        private void OnGUI()
        {
            DrawToasts();
            if (_hubVisible) DrawHub();
            ApConnectDialog.Draw();   // connect dialog (new game / continue / F9); drawn last = on top
        }

        private void DrawHub()
        {
            var area = new Rect(20, 20, 480, Screen.height - 40);
            GUILayout.BeginArea(area, GUI.skin.box);
            GUILayout.Label("Archipelago Mission Hub   [F10 to close]");
            GUILayout.Label($"Status: {Connection}   Unlocked: {CountUnlockedMissions()}/{ApData.Missions.Length} missions");
            if (_noticeHistory.Count > 0)
            {
                GUILayout.Space(4);
                GUILayout.Label("Recent activity:");
                for (int i = Mathf.Max(0, _noticeHistory.Count - 6); i < _noticeHistory.Count; i++)
                    GUILayout.Label("  " + _noticeHistory[i]);
            }
            GUILayout.Space(6);

            _hubScroll = GUILayout.BeginScrollView(_hubScroll);
            foreach (var m in ApData.Missions)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"[Act {m.Act + 1}] {m.Name}", GUILayout.Width(330));
                bool unlocked = _state.IsMissionUnlocked(m.StageID);
                if (!IsActive)
                    GUILayout.Label("(offline)");
                else if (unlocked)
                {
                    if (GUILayout.Button("Play", GUILayout.Width(90))) LaunchMission(m);
                }
                else
                    GUILayout.Label("locked", GUILayout.Width(90));
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();

            if (GUILayout.Button("Close")) _hubVisible = false;
            GUILayout.EndArea();
        }

        private int CountUnlockedMissions()
        {
            int n = 0;
            foreach (var m in ApData.Missions)
                if (_state.IsMissionUnlocked(m.StageID)) n++;
            return n;
        }

        private void LaunchMission(ApData.MissionDef m)
        {
            try
            {
                MainMod.Logger.LogInfo($"[AP] Hub launching mission {m.StageID} (folder '{m.Folder}')");
                _hubVisible = false;
                _gameStarted = true;  // launching a mission means we're in a game -> allow grants
                SyncAll();
                var folderFile = new LevelFile(m.Folder, "");   // LevelType.Official default
                Managers.Stage.LoadFlashbackMission(folderFile);
            }
            catch (System.Exception e)
            {
                MainMod.Logger.LogError($"[AP] Hub launch failed for {m.StageID}: {e}");
            }
        }

        // ----- game-session lifecycle (item application is gated on being in a game) -----

        /// <summary>Marks the New Game "Start" button as pressed so the linear first-stage load is
        /// redirected to the mission-select board (see the LoadNextStage patch). One-shot.</summary>
        public static void RequestNewGameRedirect()
        {
            if (Instance != null) Instance._newGameRedirectPending = true;
        }

        /// <summary>Consumes the one-shot new-game redirect flag (true only for the load kicked off by
        /// the Start button, so ordinary stage progression isn't affected).</summary>
        public static bool ConsumeNewGameRedirect()
        {
            if (Instance == null || !Instance._newGameRedirectPending) return false;
            Instance._newGameRedirectPending = false;
            return true;
        }

        /// <summary>Called when a game becomes active (New Game start or Continue). Enables item
        /// application and immediately flushes everything received so far -- ApState already holds
        /// every item, so a single reconcile applies perks, confidence, and abilities.</summary>
        public static void OnGameStarted()
        {
            if (Instance == null) return;
            if (!Instance._gameStarted)
                MainMod.Logger.LogInfo("[AP] Game started — applying held items.");
            Instance._gameStarted = true;
            SyncAll();
        }

        /// <summary>
        /// One full reconcile of the game's state against AP: perk-point totals, confidence,
        /// server-recorded outfit purchases, completed confidence goals, and ability perks.
        /// Every part is idempotent (top-up / set-membership based), so this is safe to run
        /// aggressively: on game start, on every item receive, before hub launches, and after
        /// every save-data (re)load (see SaveSyncPatches — the game reloads progress from disk
        /// at every level start, which wipes unsaved in-memory grants).
        ///
        /// Order matters only for confidence: boosts must be granted before RestoreOutfits can
        /// re-buy (and re-deduct the cost of) server-recorded purchases.
        /// </summary>
        public static void SyncAll()
        {
            SyncPerks();
            GrantConfidence();
            RestoreOutfits();
            RestoreCompletedGoals();
            GrantUnlockedAbilities();
        }

        /// <summary>Called when the main menu (re)loads. Pauses item application until the player
        /// starts/continues a game again; received items keep accruing in ApState meanwhile.</summary>
        public static void OnReturnToMenu()
        {
            if (Instance == null) return;
            if (Instance._gameStarted)
                MainMod.Logger.LogInfo("[AP] Returned to main menu — holding item application until a game is started.");
            Instance._gameStarted = false;
        }

        private void ApplySlotData(Dictionary<string, object> slot)
        {
            if (slot.TryGetValue("death_link", out var dl)) _state.DeathLink = System.Convert.ToBoolean(dl);
            if (slot.TryGetValue("confidence_per_boost", out var cpb)) _state.ConfidencePerBoost = System.Convert.ToInt32(cpb);
            if (slot.TryGetValue("start_character", out var sc)) _state.StartCharacter = sc?.ToString();
            if (slot.TryGetValue("start_mission", out var sm)) _state.StartMission = sm?.ToString();
            MainMod.Logger.LogInfo($"[AP] slot_data: deathlink={_state.DeathLink} confPerBoost={_state.ConfidencePerBoost} startChar={_state.StartCharacter} startMission={_state.StartMission}");
        }

        // ----- AP confidence economy -----
        // Natural confidence earning is blocked (see Patch_AddPoints); confidence comes only from
        // "Confidence Boost: <Char>" items. GetPoints() DECREASES when the player buys outfits
        // (UnlockCostume subtracts), so we can't top-up-to-total like SyncPerks. Instead we grant
        // each item's worth exactly ONCE per slot lifetime: track how much we've already granted per
        // character and apply only the positive delta. The granted totals persist in the game save
        // (piggybacking on ConfidencePointManager's save/load, see ConfidencePatches), so reconnects
        // and reloads -- which re-send every item -- never re-grant.
        private static readonly Dictionary<string, int> _confidenceGranted = new Dictionary<string, int>();

        public static void GrantConfidence()
        {
            if (!IsActive || !GameStarted || Instance == null) return;
            var cpm = Managers.ConfidencePoints;
            if (cpm == null) return;
            try
            {
                foreach (string ch in ApLookup.PlayableCharacters)
                {
                    if (!Enum.TryParse(ch, out CharacterNames cn)) continue;
                    int earned = Instance._state.ConfidenceEarnedFor(ch);
                    int granted = _confidenceGranted.TryGetValue(ch, out var g) ? g : 0;
                    int delta = earned - granted;
                    if (delta <= 0) continue;

                    Patch_AddPoints.ApGrantInProgress = true;
                    try { cpm.AddPoints(cn, delta); }
                    finally { Patch_AddPoints.ApGrantInProgress = false; }

                    _confidenceGranted[ch] = granted + delta;
                    MainMod.Logger.LogInfo($"[AP] Granted {delta} confidence to {ch} (total AP-granted {granted + delta}).");
                }
            }
            catch (Exception e)
            {
                MainMod.Logger.LogWarning($"[AP] GrantConfidence error: {e.Message}");
            }
        }

        // Persistence hooks for the AP-granted confidence totals, called from ConfidencePatches so
        // they share ConfidencePointManager's own save lifecycle (cleared on new game, loaded on
        // load, written on save) and stay bound to the exact save slot.
        public static void SaveConfidenceGranted(IDataBlockRecorder writer)
        {
            foreach (var kv in _confidenceGranted)
                if (kv.Value > 0) writer.WriteData(kv.Key + "APConfidenceGranted", kv.Value);
        }

        public static void LoadConfidenceGranted(DataBlock data)
        {
            _confidenceGranted.Clear();
            foreach (string ch in ApLookup.PlayableCharacters)
            {
                string key = ch + "APConfidenceGranted";
                if (data.ContainsKey(key)) _confidenceGranted[ch] = data.GetIntValueOrDefault(key, 0);
            }
        }

        public static void ClearConfidenceGranted() => _confidenceGranted.Clear();

        // ----- restore server-recorded state onto a (routinely reset) save -----
        // AP saves never make story progression, so players effectively start a "new game" each
        // session — wiping bought outfits, completed goals, confidence, and perk points from the
        // save. The AP server is the durable record: received items rebuild points/abilities via
        // the reconciles above, and the two restores below rebuild purchase/goal state from the
        // server's checked locations.

        /// <summary>True while <see cref="RestoreOutfits"/> is re-buying costumes, so the
        /// UnlockCostume postfix doesn't re-report those (already checked) purchase locations.</summary>
        public static bool OutfitRestoreInProgress { get; private set; }

        /// <summary>
        /// Re-applies outfit purchases recorded on the AP server (checked buyoutfit:* locations)
        /// that the current save doesn't have. Each is re-bought at its real unlockCost: the
        /// confidence granted-tracker was wiped with the save too, so boosts re-grant in FULL and
        /// deducting the price again lands the balance exactly where it was. A costume whose cost
        /// can't be covered yet (boosts still arriving) is skipped and retried on the next sync.
        /// </summary>
        public static void RestoreOutfits()
        {
            if (!IsActive || !GameStarted || Instance == null) return;
            var cpm = Managers.ConfidencePoints;
            var chars = Managers.Characters;
            if (cpm == null || chars == null) return;
            try
            {
                foreach (var lo in ApData.Locations)
                {
                    if (lo.Category != "outfit_purchase") continue;
                    if (!Instance._client.IsLocationChecked(lo.Id)) continue;
                    string rest = ApLookup.AfterColon(lo.Key);   // "<internalChar>:<saveName>"
                    int i = rest.IndexOf(':');
                    if (i <= 0) continue;
                    string ch = rest.Substring(0, i);
                    string costumeSave = rest.Substring(i + 1);
                    if (!Enum.TryParse(ch, out CharacterNames cn)) continue;
                    if (cpm.IsCostumeUnlocked(cn, costumeSave)) continue;

                    Costume costume = null;
                    foreach (var c in chars.GetCharacterCostumes(cn))
                        if (c.saveName == costumeSave) { costume = c; break; }
                    if (costume == null) continue;
                    if (cpm.GetPoints(cn) < costume.unlockCost) continue;   // boosts not applied yet -> retried

                    OutfitRestoreInProgress = true;
                    bool ok;
                    try { ok = cpm.UnlockCostume(cn, costumeSave, costume.unlockCost); }
                    finally { OutfitRestoreInProgress = false; }
                    if (ok) MainMod.Logger.LogInfo($"[AP] Restored purchased outfit '{costumeSave}' for {ch} (cost {costume.unlockCost}).");
                }
            }
            catch (Exception e)
            {
                MainMod.Logger.LogWarning($"[AP] RestoreOutfits error: {e.Message}");
            }
        }

        private static AccessTools.FieldRef<ConfidencePointManager, HashSet<string>> _completedGoalsRef;

        /// <summary>
        /// Restores the game's completed-confidence-goal set from the server's checked goal
        /// locations, so goals finished in a previous (since wiped) save show as done and aren't
        /// re-awarded. AP goal key "goal:&lt;folder&gt;/&lt;file.lvl&gt;|&lt;goalSaveName&gt;|&lt;ordinal&gt;"
        /// maps to the game's stored string "&lt;folder&gt;|&lt;file&gt;|Official|&lt;goalSaveName&gt;" via
        /// LevelFile.FromShortName (its ctor strips the .lvl extension, so the round-trip matches
        /// exactly what ProcessGoal wrote from currentFile.GetSaveDataString()).
        /// </summary>
        public static void RestoreCompletedGoals()
        {
            if (!IsActive || !GameStarted || Instance == null) return;
            var cpm = Managers.ConfidencePoints;
            if (cpm == null) return;
            try
            {
                if (_completedGoalsRef == null)
                    _completedGoalsRef = AccessTools.FieldRefAccess<ConfidencePointManager, HashSet<string>>("completedGoals");
                var completed = _completedGoalsRef(cpm);
                foreach (var lo in ApData.Locations)
                {
                    if (lo.Category != "confidence_goal") continue;
                    if (!Instance._client.IsLocationChecked(lo.Id)) continue;
                    string rest = ApLookup.AfterColon(lo.Key);   // "<level>|<goal>|<ordinal>"
                    string[] parts = rest.Split('|');
                    if (parts.Length < 3) continue;
                    string level = parts[0];
                    string goal = string.Join("|", parts, 1, parts.Length - 2);   // goal name minus trailing ordinal
                    var file = LevelFile.FromShortName(level);
                    if (!file.IsValid()) continue;
                    if (completed.Add(file.GetSaveDataString() + "|" + goal))
                        MainMod.Logger.LogInfo($"[AP] Restored completed goal '{goal}' ({level}).");
                }
            }
            catch (Exception e)
            {
                MainMod.Logger.LogWarning($"[AP] RestoreCompletedGoals error: {e.Message}");
            }
        }

        // ----- abilities -----
        // SeerFinaleKnockBackDummy is hardcoded finale-only / broken -> never AP-granted even if its
        // item arrives, and stripped if present.
        private const string BrokenAbilityPerk = "SeerFinaleKnockBackDummy";

        // Perk savenames AP controls = every `ability` item in the contract, mapped to its perk:
        // base items key by savename ("ability:seerOverwatch"); specials by stage ("ability:Unlock_X")
        // -> strip the "Unlock_" prefix. Built once. These are the perks AP owns end-to-end: the
        // vanilla grantPerk[] grant of them is blocked (Patch_AcquirePerk) so AP is the only source.
        private static HashSet<string> _apAbilityPerks;
        public static HashSet<string> ApAbilityPerks()
        {
            if (_apAbilityPerks == null)
            {
                _apAbilityPerks = new HashSet<string>();
                foreach (var it in ApData.Items)
                {
                    if (it.Category != "ability") continue;
                    string stage = ApLookup.AfterColon(it.Key);
                    _apAbilityPerks.Add(stage.StartsWith("Unlock_") ? stage.Substring("Unlock_".Length) : stage);
                }
            }
            return _apAbilityPerks;
        }

        public static bool IsApControlledAbilityPerk(string perkSaveName)
            => !string.IsNullOrEmpty(perkSaveName) && ApAbilityPerks().Contains(perkSaveName);

        /// <summary>
        /// Reconcile each wizard's AP-controlled ability perks to EXACTLY the set the server has sent.
        /// Abilities are no longer granted at start, and the vanilla `grantPerk[]` level grants are
        /// blocked (Patch_AcquirePerk), so AP is the only source. Acquire perks whose AP item arrived;
        /// un-acquire any that are present without one (cleans dirty saves / abilities the game granted
        /// before the block). Item key "ability:&lt;stageID&gt;": specials "Unlock_&lt;X&gt;", base = savename.
        /// </summary>
        public static void GrantUnlockedAbilities()
        {
            if (!IsActive || !GameStarted || Instance == null) return;
            var pm = Managers.Perks;
            if (pm == null) return;
            try
            {
                var owned = Instance._state.UnlockedAbilities;
                foreach (string savename in ApAbilityPerks())
                {
                    var perk = pm.GetByName(savename);
                    if (perk == null) continue;
                    bool shouldHave = savename != BrokenAbilityPerk
                        && (owned.Contains(savename) || owned.Contains("Unlock_" + savename));
                    bool has = pm.IsAcquired(perk);

                    if (shouldHave && !has)
                    {
                        Patch_AcquirePerk.ApGrantInProgress = true;
                        try { pm.AcquirePerk(perk); }
                        finally { Patch_AcquirePerk.ApGrantInProgress = false; }
                        MainMod.Logger.LogInfo($"[AP] Granted ability '{savename}' ({perk.ApplicableCharacter}) from AP item.");
                    }
                    else if (!shouldHave && has)
                    {
                        pm.UnacquirePerk(perk);
                        MainMod.Logger.LogInfo($"[AP] Removed non-AP ability '{savename}' ({perk.ApplicableCharacter}).");
                    }
                }
            }
            catch (Exception e)
            {
                MainMod.Logger.LogWarning($"[AP] GrantUnlockedAbilities error: {e.Message}");
            }
        }


        /// <summary>
        /// Reconcile the game's perk points with AP: top up each character's perk points to match
        /// the number of received perk_point items. Total-based, so it never re-grants spent points
        /// and survives save/reload (spending lowers spendable but leaves the total).
        ///
        /// NOTE: we intentionally do NOT auto-grant campaign perks here — those are the per-character
        /// anxiety-dream story-reward perks, not core abilities. Core abilities (Predictive Bolt /
        /// Time Boost) come from elsewhere and are deferred; the plan is to randomize abilities (and
        /// likely these story perks) into the AP item pool later.
        /// </summary>
        public static void SyncPerks()
        {
            if (!IsActive || !GameStarted || Instance == null) return;
            var pm = Managers.Perks;
            if (pm == null) return;
            try
            {
                foreach (string ch in ApLookup.PlayableCharacters)
                {
                    if (!Enum.TryParse(ch, out CharacterNames cn)) continue;
                    int desired = Instance._state.PerkPointsFor(ch);
                    int current = pm.GetTotalPerkPoints(cn);
                    if (desired > current)
                    {
                        Patch_AddPerkPoints.ApGrantInProgress = true;
                        try { pm.AddPerkPoints(cn, desired - current); }
                        finally { Patch_AddPerkPoints.ApGrantInProgress = false; }
                        MainMod.Logger.LogInfo($"[AP] Granted {desired - current} perk point(s) to {ch} (total {desired}).");
                    }
                }
            }
            catch (Exception e)
            {
                MainMod.Logger.LogWarning($"[AP] SyncPerks error: {e.Message}");
            }
        }

        // ----- check reporting (called from Harmony patches; safe no-ops when inactive) -----
        private void Send(long? locId, string label)
        {
            if (!IsActive || locId == null) return;
            if (!_sentChecks.Add(locId.Value)) return; // local dedup
            MainMod.Logger.LogInfo($"[AP] Check: {label} (loc {locId.Value})");
            _client.SendChecks(new[] { locId.Value });
        }

        public static void ReportMissionComplete(string stageID)
        {
            if (Instance == null) return;
            Instance.Send(ApLookup.MissionCompleteId(stageID), "mission:" + stageID);
            // Completing a mission -- linear campaign or hub flashback -- also fires the recruit
            // check of the character it recruits. The map is generated into ApData from the
            // contract's recruit locations, so it always matches the apworld's logic (including
            // RECRUIT_OVERRIDE moves like Banks recruiting at The Blacksite).
            if (ApData.RecruitByMission.TryGetValue(stageID, out string recruited))
                ReportRecruit(recruited);
            if (stageID == "Game_Finale_Roof" && IsActive)
                Instance._client.SendGoal();
        }

        /// <summary>Whether this mission's completion has been registered with AP -- either the
        /// game's own stage-complete flag (linear campaign) or the AP mission-complete location being
        /// checked (locally this session, or on the server after a reconnect). AP missions finish via
        /// flashback, which never sets the game's IsStageCompleted, so the location check is the
        /// reliable signal.</summary>
        public static bool IsMissionCompleted(string stageID)
        {
            if (Instance == null) return false;
            long? loc = ApLookup.MissionCompleteId(stageID);
            if (loc == null) return false;
            return Instance._sentChecks.Contains(loc.Value) || Instance._client.IsLocationChecked(loc.Value);
        }

        /// <summary>Fires a mission's HALFWAY checkpoint check (multi-room missions only). Missions
        /// with a single room have no mission_half location, so the id lookup returns null and Send
        /// no-ops. Called when the mission's halfway .lvl (MissionDef.HalfLevel) completes.</summary>
        public static void ReportMissionHalf(string stageID)
            => Instance?.Send(ApLookup.MissionHalfId(stageID), "missionhalf:" + stageID);

        public static void ReportConfidenceGoal(string level, string goalName, int ordinal)
            => Instance?.Send(ApLookup.ConfidenceGoalId(level, goalName, ordinal), $"goal:{level}|{goalName}|{ordinal}");

        public static void ReportAbilityUnlock(string stageID)
            => Instance?.Send(ApLookup.AbilityUnlockId(stageID), "ability:" + stageID);

        public static void ReportRecruit(string internalName)
            => Instance?.Send(ApLookup.RecruitId(internalName), "recruit:" + internalName);

        public static void ReportOutfitPurchase(string internalName, string saveName)
            => Instance?.Send(ApLookup.OutfitPurchaseId(internalName, saveName), $"buyoutfit:{internalName}:{saveName}");

        // ----- DeathLink (see DeathLinkPatches for the turn hooks + custom condition) -----

        /// <summary>Incoming DeathLinks not yet handed to a wizard.</summary>
        public static int PendingDeathCount => Instance?._pendingDeaths.Count ?? 0;

        public static bool TryDequeuePendingDeath(out string cause)
        {
            cause = null;
            if (Instance == null || Instance._pendingDeaths.Count == 0) return false;
            cause = Instance._pendingDeaths.Dequeue();
            return true;
        }

        /// <summary>Send a DeathLink to the multiworld. No-ops unless connected with death_link on.</summary>
        public static void SendDeathLink(string cause)
        {
            if (Instance == null || !IsActive || !Instance._state.DeathLink) return;
            Instance._client.SendDeath(cause);
            Instance.AddToast(new ApNotice(ApNoticeKind.Death, $"DeathLink sent: {cause}"));
            MainMod.Logger.LogInfo($"[AP] DeathLink sent: {cause}");
        }

        /// <summary>Toast a DeathLink event from the turn patches (AddToast is instance-private).</summary>
        public static void ToastDeath(string text)
            => Instance?.AddToast(new ApNotice(ApNoticeKind.Death, text));

        // ----- outfit shop scouting: rename shop entries to the AP item they hold + hint on open -----
        // Outfit-purchase locations we've already scouted + hinted this session (avoid re-requesting).
        private readonly HashSet<long> _hintedOutfits = new HashSet<long>();

        /// <summary>Outfit-purchase location ids for wizards the player has unlocked. Parses the
        /// location key "buyoutfit:&lt;internalChar&gt;:&lt;saveName&gt;" and keeps only checks whose
        /// wizard is in AP-unlocked state (the shop only shows those wizards anyway).</summary>
        private List<long> UnlockedOutfitLocationIds()
        {
            var ids = new List<long>();
            foreach (var lo in ApData.Locations)
            {
                if (lo.Category != "outfit_purchase") continue;
                string rest = ApLookup.AfterColon(lo.Key);   // "<internalChar>:<saveName>"
                int i = rest.IndexOf(':');
                string ch = i < 0 ? rest : rest.Substring(0, i);
                if (_state.IsCharacterUnlocked(ch)) ids.Add(lo.Id);
            }
            return ids;
        }

        /// <summary>Called when the outfit shop opens. For each unlocked wizard's outfit-purchase
        /// location we haven't handled yet this session, scouts it (so the entry can display which AP
        /// item the purchase releases) and creates a one-time server hint. Each location is scouted +
        /// hinted exactly once; scouted names persist, so re-opening the shop only touches outfits for
        /// wizards unlocked since the last open.</summary>
        public static void OnOutfitShopOpened()
        {
            if (!IsActive || Instance == null) return;
            var toHint = new List<long>();
            foreach (long id in Instance.UnlockedOutfitLocationIds())
                if (Instance._hintedOutfits.Add(id)) toHint.Add(id);
            if (toHint.Count == 0) return;
            Instance._client.ScoutLocations(true, toHint.ToArray());
            MainMod.Logger.LogInfo($"[AP] Outfit shop opened: scouting + hinting {toHint.Count} outfit check(s) for unlocked wizards.");
        }

        /// <summary>The AP item label sitting on a given outfit-purchase location (for the shop entry),
        /// or null if AP is inactive, the costume isn't an AP check (free default / DLC), or the scout
        /// hasn't returned yet.</summary>
        public static string OutfitDisplayName(string internalChar, string saveName)
        {
            if (!IsActive || Instance == null) return null;
            long? loc = ApLookup.OutfitPurchaseId(internalChar, saveName);
            if (loc == null) return null;
            return Instance._client.TryGetScoutedName(loc.Value, out var name) ? name : null;
        }
    }
}
