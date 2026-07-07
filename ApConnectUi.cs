using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Wizards.UI;

namespace Tactical_Breach_Wizards_Archipelago_Mod
{
    /// <summary>
    /// Modal connect dialog shown whenever the player enters a save (new game confirm, save-slot
    /// continue, or the title-screen Play button) while not connected to Archipelago, plus F9 as a
    /// standalone connection panel. Replaces hand-editing the BepInEx config: the config entries
    /// now just persist the last-used values, which prefill the fields and are written back on
    /// every connect attempt.
    ///
    /// The intercepted game action is captured as <see cref="_resume"/> and invoked after a
    /// successful connect, or immediately if the player picks "Play without Archipelago"; Cancel
    /// discards it. While the dialog is visible the menu's EventSystem is disabled so clicks on
    /// the IMGUI window can't also press the uGUI buttons behind it.
    /// </summary>
    public static class ApConnectDialog
    {
        private const float Width = 440f;

        private static bool _visible;
        private static string _title = "Archipelago";
        private static string _host = "", _port = "", _slot = "", _password = "";
        private static string _error;
        private static bool _connectPending;   // a connect started from this dialog is in flight
        private static Action _resume;         // held game action; null when opened standalone (F9)
        private static UnityEngine.EventSystems.EventSystem _blockedEventSystem;

        public static bool Visible => _visible;

        /// <summary>Open the dialog gating a game action. <paramref name="resume"/> runs after a
        /// successful connect or on "Play without Archipelago"; Cancel drops it.</summary>
        public static void Show(string title, Action resume)
        {
            _title = title;
            _resume = resume;
            _error = null;
            _connectPending = false;
            _host = MainMod.CfgHost;
            _port = MainMod.CfgPort.ToString();
            _slot = MainMod.CfgSlot;
            _password = MainMod.CfgPassword;
            _visible = true;
            BlockMenuInput(true);
        }

        public static void Close()
        {
            _visible = false;
            _resume = null;
            _connectPending = false;
            BlockMenuInput(false);
        }

        /// <summary>F9: open (or close) the dialog with no game action attached — lets the player
        /// connect from anywhere, or inspect/disconnect the current connection.</summary>
        public static void ToggleStandalone()
        {
            if (_visible) Close();
            else Show("Archipelago Connection", null);
        }

        // uGUI clicks pass straight through IMGUI, so a click on "Connect" would also press
        // whatever menu button sits underneath. Disabling the scene's EventSystem while the
        // dialog is up makes it properly modal; the reference is kept so Close() re-enables
        // exactly the one we disabled (scene loads bring their own fresh EventSystem).
        private static void BlockMenuInput(bool block)
        {
            try
            {
                if (block)
                {
                    var es = UnityEngine.EventSystems.EventSystem.current;
                    if (es != null) { es.enabled = false; _blockedEventSystem = es; }
                }
                else if (_blockedEventSystem != null)
                {
                    _blockedEventSystem.enabled = true;
                    _blockedEventSystem = null;
                }
            }
            catch (Exception e)
            {
                MainMod.Logger.LogWarning($"[AP] EventSystem toggle failed: {e.Message}");
            }
        }

        /// <summary>Drawn every frame from <see cref="ApManager.OnGUI"/> (after toasts/hub so the
        /// dialog is on top). Also polls the in-flight connect and fires the resume on success.</summary>
        public static void Draw()
        {
            if (!_visible) return;

            if (_connectPending)
            {
                var st = ApManager.Connection;
                if (st == ApConnectionState.Connected)
                {
                    _connectPending = false;
                    var resume = _resume;
                    Close();
                    resume?.Invoke();
                    return;
                }
                if (st == ApConnectionState.Failed || st == ApConnectionState.Disconnected)
                {
                    _connectPending = false;
                    _error = ApManager.LastError ?? "Connection failed.";
                }
            }

            bool connected = ApManager.Connection == ApConnectionState.Connected;

            var ev = Event.current;
            if (ev.type == EventType.KeyDown)
            {
                if (ev.keyCode == KeyCode.Escape) { ev.Use(); Close(); return; }
                if (!connected && !_connectPending &&
                    (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter))
                {
                    ev.Use();
                    StartConnect();
                }
            }

            float height = connected ? 140f : 270f;
            var rect = new Rect((Screen.width - Width) / 2f, (Screen.height - height) / 2f, Width, height);
            GUILayout.BeginArea(rect, _title, GUI.skin.window);
            GUILayout.Space(10);

            if (connected)
            {
                GUILayout.Label($"Connected to {MainMod.CfgHost}:{MainMod.CfgPort} as '{MainMod.CfgSlot}'.");
                GUILayout.FlexibleSpace();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Disconnect")) { ApManager.Disconnect(); _error = null; }
                if (GUILayout.Button("Close")) Close();
                GUILayout.EndHorizontal();
            }
            else
            {
                TextRow("Server", ref _host);
                TextRow("Port", ref _port);
                TextRow("Slot name", ref _slot);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Password", GUILayout.Width(90));
                _password = GUILayout.PasswordField(_password ?? "", '*');
                GUILayout.EndHorizontal();

                GUILayout.Space(6);
                if (_connectPending) StatusLabel("Connecting...", new Color(0.65f, 0.9f, 1f));
                else if (_error != null) StatusLabel(_error, new Color(1f, 0.55f, 0.45f));

                GUILayout.FlexibleSpace();
                GUI.enabled = !_connectPending;
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Connect")) StartConnect();
                if (_resume != null && GUILayout.Button("Play without Archipelago"))
                {
                    var resume = _resume;
                    Close();
                    resume?.Invoke();
                }
                if (GUILayout.Button("Cancel")) Close();
                GUILayout.EndHorizontal();
                GUI.enabled = true;
            }
            GUILayout.EndArea();
        }

        private static void TextRow(string label, ref string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(90));
            value = GUILayout.TextField(value ?? "");
            GUILayout.EndHorizontal();
        }

        private static void StatusLabel(string text, Color color)
        {
            var style = new GUIStyle(GUI.skin.label) { wordWrap = true };
            style.normal.textColor = color;
            GUILayout.Label(text, style);
        }

        private static void StartConnect()
        {
            _error = null;
            string host = (_host ?? "").Trim();
            string slot = (_slot ?? "").Trim();
            string portText = (_port ?? "").Trim();

            // Convenience: a pasted "host:port" in the server field fills both.
            int colon = host.LastIndexOf(':');
            if (colon > 0)
            {
                portText = host.Substring(colon + 1);
                host = host.Substring(0, colon);
                _host = host;
                _port = portText;
            }

            if (host.Length == 0) { _error = "Server is required."; return; }
            if (!int.TryParse(portText, out int port) || port < 1 || port > 65535)
            { _error = "Port must be a number between 1 and 65535."; return; }
            if (slot.Length == 0) { _error = "Slot name is required."; return; }

            // Persist immediately so the next session prefills what was typed.
            MainMod.CfgHost = host;
            MainMod.CfgPort = port;
            MainMod.CfgSlot = slot;
            MainMod.CfgPassword = _password ?? "";

            ApManager.EnsureExists().Connect(host, port, slot, _password ?? "");
            _connectPending = true;
        }
    }

    // ----- gates: every path into a save opens the dialog first when disconnected -----
    // Each gate blocks the original method, hands it to the dialog as the resume action, and
    // re-invokes it with a one-shot _resuming flag so the second pass falls through. All three
    // entry points into gameplay are covered: new-game confirm, save-slot continue, and the
    // title-screen Play button (which continues the most recent save via PlayZoom).

    /// <summary>
    /// New Game confirm (after slot + difficulty pick — the actual new-game entry point). When
    /// disconnected, holds the confirm behind the connect dialog. When connected (or resumed),
    /// flags the mission-select redirect (see StagePatches.Patch_RedirectNewGameToMissionSelect)
    /// and lets the original run its reset/save/difficulty setup.
    /// </summary>
    [HarmonyPatch(typeof(SaveSlotEntry), "OnNewGameConfirm")]
    public static class Patch_GateNewGame
    {
        private static readonly MethodInfo Original = AccessTools.Method(typeof(SaveSlotEntry), "OnNewGameConfirm");
        private static bool _resuming;

        [HarmonyPrefix]
        public static bool Prefix(SaveSlotEntry __instance, Difficulty.Mode _difficulty)
        {
            if (ApManager.IsActive || _resuming)
            {
                _resuming = false;
                if (ApManager.IsActive) ApManager.RequestNewGameRedirect();
                return true;
            }
            var entry = __instance;
            var difficulty = _difficulty;
            ApConnectDialog.Show("New Game — Connect to Archipelago", () =>
            {
                _resuming = true;
                try { Original.Invoke(entry, new object[] { difficulty }); }
                catch (Exception e) { _resuming = false; MainMod.Logger.LogError($"[AP] New-game resume failed: {e}"); }
            });
            return false;
        }
    }

    /// <summary>Save-slot Continue button: same gate, resuming the vanilla continue flow.</summary>
    [HarmonyPatch(typeof(SaveSlotEntry), "OnContinueClick")]
    public static class Patch_GateContinueSlot
    {
        private static readonly MethodInfo Original = AccessTools.Method(typeof(SaveSlotEntry), "OnContinueClick");
        private static bool _resuming;

        [HarmonyPrefix]
        public static bool Prefix(SaveSlotEntry __instance)
        {
            if (ApManager.IsActive || _resuming) { _resuming = false; return true; }
            var entry = __instance;
            ApConnectDialog.Show("Continue — Connect to Archipelago", () =>
            {
                _resuming = true;
                try { Original.Invoke(entry, null); }
                catch (Exception e) { _resuming = false; MainMod.Logger.LogError($"[AP] Continue resume failed: {e}"); }
            });
            return false;
        }
    }

    /// <summary>Title-screen Play button, which zooms and continues the most recent save
    /// (PlayZoom -> StageManager.ContinueGame) without going through the save-slot panel.</summary>
    [HarmonyPatch(typeof(PlayPanel), "OnPlayClick")]
    public static class Patch_GateTitlePlay
    {
        private static readonly MethodInfo Original = AccessTools.Method(typeof(PlayPanel), "OnPlayClick");
        private static bool _resuming;

        [HarmonyPrefix]
        public static bool Prefix(PlayPanel __instance)
        {
            if (ApManager.IsActive || _resuming) { _resuming = false; return true; }
            var panel = __instance;
            ApConnectDialog.Show("Continue — Connect to Archipelago", () =>
            {
                _resuming = true;
                try { Original.Invoke(panel, null); }
                catch (Exception e) { _resuming = false; MainMod.Logger.LogError($"[AP] Play resume failed: {e}"); }
            });
            return false;
        }
    }

    /// <summary>Vanilla stage-replay entry ("Mission Select" on a save slot, which jumps straight
    /// to a stage via OverwriteProgressAndJumpToStage -> ContinueGame). Same gate.</summary>
    [HarmonyPatch(typeof(SaveSlotEntry), "OnMissionSelectConfirm")]
    public static class Patch_GateMissionSelect
    {
        private static readonly MethodInfo Original = AccessTools.Method(typeof(SaveSlotEntry), "OnMissionSelectConfirm");
        private static bool _resuming;

        [HarmonyPrefix]
        public static bool Prefix(SaveSlotEntry __instance)
        {
            if (ApManager.IsActive || _resuming) { _resuming = false; return true; }
            var entry = __instance;
            ApConnectDialog.Show("Mission Select — Connect to Archipelago", () =>
            {
                _resuming = true;
                try { Original.Invoke(entry, null); }
                catch (Exception e) { _resuming = false; MainMod.Logger.LogError($"[AP] Mission-select resume failed: {e}"); }
            });
            return false;
        }
    }
}
