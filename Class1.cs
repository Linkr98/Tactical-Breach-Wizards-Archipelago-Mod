using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace Tactical_Breach_Wizards_Archipelago_Mod
{
    // Define your mod info (GUID, Name, Version)
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class MainMod : BaseUnityPlugin
    {
        public const string PluginGuid = "com.lincoln.tbwap";
        public const string PluginName = "Tactical Breach Wizards Archipelago";
        public const string PluginVersion = "0.20.0";

        internal static MainMod Instance;
        internal static new ManualLogSource Logger;

        // Connection details are entered through the in-game dialog (ApConnectDialog); these
        // entries just persist the last-used values so the dialog prefills across sessions.
        private static ConfigEntry<string> _host;
        private static ConfigEntry<int> _port;
        private static ConfigEntry<string> _slot;
        private static ConfigEntry<string> _password;

        internal static string CfgHost { get => _host.Value; set => _host.Value = value; }
        internal static int CfgPort { get => _port.Value; set => _port.Value = value; }
        internal static string CfgSlot { get => _slot.Value; set => _slot.Value = value; }
        internal static string CfgPassword { get => _password.Value; set => _password.Value = value; }

        private void Awake()
        {
            Instance = this;
            Logger = base.Logger;

            _host = Config.Bind("Archipelago", "Host", "localhost", "Last-used server host (set from the in-game connect dialog).");
            _port = Config.Bind("Archipelago", "Port", 38281, "Last-used server port (set from the in-game connect dialog).");
            _slot = Config.Bind("Archipelago", "SlotName", "", "Last-used player/slot name (set from the in-game connect dialog).");
            _password = Config.Bind("Archipelago", "Password", "", "Last-used server password (set from the in-game connect dialog).");

            var harmony = new Harmony(PluginGuid);
            harmony.PatchAll();
            SocketCompressionPatch.Apply(harmony);

            ApManager.EnsureExists();

            Logger.LogInfo($"{PluginName} v{PluginVersion} injected. Connect dialog opens when starting/continuing a save. " +
                           "F8 = dump, F9 = connection panel.");
        }
    }

    // Main-menu hook: this fires reliably (it drove the data dump). We also (re)create the
    // manager here in a proper runtime context. Connecting happens through ApConnectDialog,
    // which opens when the player starts/continues a save (or presses F9).
    [HarmonyPatch(typeof(Wizards.UI.MainMenuScene), "Start")]
    public class MainMenuPatch
    {
        private static bool _dumped;

        [HarmonyPostfix]
        public static void Postfix()
        {
            ApManager.EnsureExists();
            // Back at the main menu: pause item application until a game is started/continued again.
            ApManager.OnReturnToMenu();

            if (!_dumped)
            {
                _dumped = true;
                DataDumper.DumpAll("main menu load");
            }
        }
    }
}
