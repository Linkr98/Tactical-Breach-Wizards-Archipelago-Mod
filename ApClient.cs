using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.BounceFeatures.DeathLink;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Helpers;
using Archipelago.MultiClient.Net.MessageLog.Messages;
using Archipelago.MultiClient.Net.Models;

namespace Tactical_Breach_Wizards_Archipelago_Mod
{
    public enum ApConnectionState { Disconnected, Connecting, Connected, Failed }

    public enum ApNoticeKind { Received, Sent, Found, Death, Info }

    /// <summary>One displayable AP event (item sent/received/found, deathlink, status) for the
    /// on-screen toast feed. Built on the socket thread, drained on the main thread.</summary>
    public struct ApNotice
    {
        public ApNoticeKind Kind;
        public string Text;
        public ItemFlags Flags;   // progression/useful/trap -> toast color

        public ApNotice(ApNoticeKind kind, string text, ItemFlags flags = ItemFlags.None)
        {
            Kind = kind; Text = text; Flags = flags;
        }
    }

    /// <summary>
    /// Thin wrapper around Archipelago.MultiClient.Net. Connection runs on a background
    /// thread; received item ids are pushed onto a thread-safe queue that the Unity main
    /// thread drains (see <see cref="ApManager"/>). Knows nothing about game state.
    /// </summary>
    public class ApClient
    {
        public const string GameName = "Tactical Breach Wizards";

        private ArchipelagoSession _session;
        private DeathLinkService _deathLink;

        public ApConnectionState Connection { get; private set; } = ApConnectionState.Disconnected;
        public string LastError { get; private set; }
        public Dictionary<string, object> SlotData { get; private set; }

        // Background -> main-thread handoff.
        public readonly ConcurrentQueue<long> IncomingItems = new ConcurrentQueue<long>();
        public readonly ConcurrentQueue<string> IncomingDeaths = new ConcurrentQueue<string>();
        public readonly ConcurrentQueue<ApNotice> IncomingNotices = new ConcurrentQueue<ApNotice>();

        // location id -> display label of the item sitting on it (populated by ScoutLocations).
        private readonly ConcurrentDictionary<long, string> _scoutedNames = new ConcurrentDictionary<long, string>();

        public event Action OnConnectedMainThreadHint;  // optional; ApManager polls state anyway

        public void Connect(string host, int port, string slot, string password)
        {
            if (Connection == ApConnectionState.Connecting || Connection == ApConnectionState.Connected)
            {
                MainMod.Logger.LogWarning("[AP] Already connecting/connected.");
                return;
            }
            Connection = ApConnectionState.Connecting;
            LastError = null;

            var t = new Thread(() => ConnectBlocking(host, port, slot, password)) { IsBackground = true };
            t.Start();
        }

        private void ConnectBlocking(string host, int port, string slot, string password)
        {
            try
            {
                _session = ArchipelagoSessionFactory.CreateSession(host, port);
                _session.Items.ItemReceived += OnItemReceived;
                _session.MessageLog.OnMessageReceived += OnLogMessage;
                _session.Socket.ErrorReceived += (e, msg) => MainMod.Logger.LogWarning($"[AP] socket error: {msg}");
                _session.Socket.SocketClosed += reason =>
                {
                    MainMod.Logger.LogWarning($"[AP] socket closed: {reason}");
                    Connection = ApConnectionState.Disconnected;
                };

                LoginResult result = _session.TryConnectAndLogin(
                    GameName, slot, ItemsHandlingFlags.AllItems,
                    password: string.IsNullOrEmpty(password) ? null : password,
                    requestSlotData: true);

                if (result is LoginSuccessful success)
                {
                    SlotData = success.SlotData ?? new Dictionary<string, object>();
                    SetupDeathLink();
                    Connection = ApConnectionState.Connected;
                    MainMod.Logger.LogInfo($"[AP] Connected as '{slot}' to {host}:{port}.");
                    OnConnectedMainThreadHint?.Invoke();
                }
                else if (result is LoginFailure failure)
                {
                    LastError = string.Join("; ", failure.Errors ?? new[] { "unknown error" });
                    Connection = ApConnectionState.Failed;
                    MainMod.Logger.LogError($"[AP] Login failed: {LastError}");
                }
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Connection = ApConnectionState.Failed;
                MainMod.Logger.LogError($"[AP] Connection threw: {e}");
            }
        }

        private void OnItemReceived(ReceivedItemsHelper helper)
        {
            // Fires on the socket thread; just hand ids to the main thread.
            while (helper.Any())
            {
                ItemInfo item = helper.DequeueItem();
                IncomingItems.Enqueue(item.ItemId);
            }
        }

        /// <summary>Turns server log traffic into toast notices. Only live item movement involving
        /// this slot is kept: "Sent X to P" (we checked a location holding someone else's item),
        /// "Received X from P" (someone released one of ours), "Found X" (our own item on our own
        /// check). Hint messages are skipped — the outfit-shop scouts create hints on demand and
        /// would spam the feed. Fires on the socket thread; notices are drained by ApManager.
        /// Note: items granted while the client was DISCONNECTED arrive via the login backlog, which
        /// has no log message — those are applied silently (no toast) rather than replayed as spam.</summary>
        private void OnLogMessage(LogMessage message)
        {
            try
            {
                if (message is HintItemSendLogMessage) return;
                var send = message as ItemSendLogMessage;
                if (send == null || (!send.IsReceiverTheActivePlayer && !send.IsSenderTheActivePlayer)) return;

                string item = send.Item.ItemDisplayName;
                ApNotice notice;
                if (send.IsReceiverTheActivePlayer && send.IsSenderTheActivePlayer)
                    notice = new ApNotice(ApNoticeKind.Found, $"Found {item}  ({send.Item.LocationDisplayName})", send.Item.Flags);
                else if (send.IsReceiverTheActivePlayer)
                    notice = new ApNotice(ApNoticeKind.Received, $"Received {item}  (from {PlayerLabel(send.Sender)})", send.Item.Flags);
                else
                    notice = new ApNotice(ApNoticeKind.Sent, $"Sent {item}  (to {PlayerLabel(send.Receiver)})", send.Item.Flags);
                IncomingNotices.Enqueue(notice);
            }
            catch (Exception e)
            {
                MainMod.Logger.LogWarning($"[AP] MessageLog handler error: {e.Message}");
            }
        }

        private static string PlayerLabel(PlayerInfo p)
        {
            string s = p?.Alias;
            if (string.IsNullOrEmpty(s)) s = p?.Name;
            return string.IsNullOrEmpty(s) ? "someone" : s;
        }

        private void SetupDeathLink()
        {
            try
            {
                _deathLink = _session.CreateDeathLinkService();
                _deathLink.OnDeathLinkReceived += dl => IncomingDeaths.Enqueue(dl?.Cause ?? "");
                bool enabled = SlotData != null
                    && SlotData.TryGetValue("death_link", out var v)
                    && Convert.ToBoolean(v);
                if (enabled) _deathLink.EnableDeathLink();
            }
            catch (Exception e)
            {
                MainMod.Logger.LogWarning($"[AP] DeathLink setup skipped: {e.Message}");
            }
        }

        // ----- outbound (call from main thread) -----
        public void SendChecks(IEnumerable<long> locationIds)
        {
            if (Connection != ApConnectionState.Connected || _session == null) return;
            var arr = new List<long>(locationIds).ToArray();
            if (arr.Length == 0) return;
            try { _session.Locations.CompleteLocationChecks(arr); }
            catch (Exception e) { MainMod.Logger.LogWarning($"[AP] CompleteLocationChecks failed: {e.Message}"); }
        }

        public void SendCheck(long locationId) => SendChecks(new[] { locationId });

        /// <summary>The scouted display label ("ItemName" or "ItemName -> Player") for a location, or
        /// null if it hasn't been scouted yet. Filled asynchronously by <see cref="ScoutLocations"/>.</summary>
        public bool TryGetScoutedName(long locationId, out string name) => _scoutedNames.TryGetValue(locationId, out name);

        /// <summary>Scout the given locations to learn which item sits on each (for display), and
        /// optionally create a server hint for each. Runs async off a background thread; results land
        /// in <see cref="_scoutedNames"/> for the main thread to read. CreateAndAnnounceOnce makes the
        /// hint fire only once per location on the server, so re-opening the shop won't spam.</summary>
        public void ScoutLocations(bool createHints, params long[] ids)
        {
            if (Connection != ApConnectionState.Connected || _session == null || ids == null || ids.Length == 0) return;
            try
            {
                var policy = createHints ? HintCreationPolicy.CreateAndAnnounceOnce : HintCreationPolicy.None;
                _session.Locations.ScoutLocationsAsync(policy, ids).ContinueWith(task =>
                {
                    if (task.IsFaulted || task.Result == null)
                    {
                        MainMod.Logger.LogWarning($"[AP] ScoutLocations failed: {task.Exception?.GetBaseException().Message}");
                        return;
                    }
                    foreach (var kv in task.Result)
                    {
                        ScoutedItemInfo info = kv.Value;
                        string label = info.ItemName ?? $"Item {info.ItemId}";
                        if (!info.IsReceiverRelatedToActivePlayer)
                        {
                            string who = info.Player?.Alias ?? info.Player?.Name;
                            if (!string.IsNullOrEmpty(who)) label += " -> " + who;
                        }
                        _scoutedNames[kv.Key] = label;
                    }
                });
            }
            catch (Exception e)
            {
                MainMod.Logger.LogWarning($"[AP] ScoutLocations error: {e.Message}");
            }
        }

        /// <summary>True if the server has recorded this location as checked. Survives reconnects
        /// (the server restores the checked set on login), unlike the in-session local dedup.</summary>
        public bool IsLocationChecked(long locationId)
        {
            try { return _session?.Locations?.AllLocationsChecked?.Contains(locationId) ?? false; }
            catch { return false; }
        }

        public void SendGoal()
        {
            if (Connection != ApConnectionState.Connected || _session == null) return;
            try { _session.SetGoalAchieved(); MainMod.Logger.LogInfo("[AP] Goal achieved sent."); }
            catch (Exception e) { MainMod.Logger.LogWarning($"[AP] SetGoalAchieved failed: {e.Message}"); }
        }

        public void SendDeath(string cause)
        {
            try { _deathLink?.SendDeathLink(new DeathLink(GetSlotName(), cause)); }
            catch (Exception e) { MainMod.Logger.LogWarning($"[AP] SendDeathLink failed: {e.Message}"); }
        }

        private string GetSlotName()
        {
            try { return _session?.Players?.GetPlayerAlias(_session.ConnectionInfo.Slot) ?? "TBW"; }
            catch { return "TBW"; }
        }

        public void Disconnect()
        {
            try { _session?.Socket?.DisconnectAsync(); } catch { }
            Connection = ApConnectionState.Disconnected;
        }
    }
}
