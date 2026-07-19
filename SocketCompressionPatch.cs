using System;
using HarmonyLib;
using WebSocketSharp;

namespace Tactical_Breach_Wizards_Archipelago_Mod
{
    /// <summary>
    /// Enables permessage-deflate websocket compression on the Archipelago connection.
    /// The AP server warns (and will eventually refuse) clients that connect uncompressed.
    /// Archipelago.MultiClient.Net never enables compression itself: its net45 build uses
    /// ClientWebSocket (which can't compress on .NET Framework at all), so we ship the net40
    /// build instead, whose websocket-sharp backend supports deflate — it just defaults to
    /// off. Two patches are needed:
    ///   1. Postfix the lib's CreateWebSocket to turn compression on.
    ///   2. Replace websocket-sharp's Sec-WebSocket-Extensions response validation, which
    ///      rejects the whole handshake when the server's reply contains any parameter it
    ///      doesn't know. The AP server (python `websockets` with server_max_window_bits=11)
    ///      always adds server_max_window_bits, which only constrains the server's OWN
    ///      compressor — our 32k-window decompressor handles it regardless, so it's safe to
    ///      accept and ignore.
    /// Applied manually (not via PatchAll) so a lib-internals change degrades to the old
    /// server warning instead of breaking the whole plugin.
    /// </summary>
    internal static class SocketCompressionPatch
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                harmony.Patch(
                    AccessTools.Method(
                        "Archipelago.MultiClient.Net.Helpers.ArchipelagoSocketHelper:CreateWebSocket"),
                    postfix: new HarmonyMethod(
                        typeof(SocketCompressionPatch), nameof(EnableCompression)));
                harmony.Patch(
                    AccessTools.Method(
                        typeof(WebSocket), "validateSecWebSocketExtensionsServerHeader"),
                    prefix: new HarmonyMethod(
                        typeof(SocketCompressionPatch), nameof(LenientValidateExtensions)));
            }
            catch (Exception e)
            {
                MainMod.Logger.LogWarning($"[AP] Couldn't enable websocket compression (server will warn about it): {e.Message}");
            }
        }

        private static void EnableCompression(WebSocket __result)
        {
            __result.Compression = CompressionMethod.Deflate;
        }

        /// <summary>Replaces the original validator (skips it by returning false). Same rules,
        /// except server_max_window_bits=N is tolerated. client_max_window_bits stays rejected:
        /// honoring it would need a smaller compression window than websocket-sharp uses, and a
        /// compliant server never sends it since we don't offer it.</summary>
        private static bool LenientValidateExtensions(string value, ref bool __result)
        {
            __result = IsAcceptableExtensionsHeader(value);
            return false;
        }

        private static bool IsAcceptableExtensionsHeader(string value)
        {
            // No header: the server declined compression; the connection proceeds uncompressed.
            if (value == null)
                return true;
            if (value.Trim().Length == 0)
                return false;

            foreach (string rawExt in value.Split(','))
            {
                string ext = rawExt.Trim();
                if (!ext.StartsWith("permessage-deflate", StringComparison.Ordinal))
                    return false;   // we only ever offer permessage-deflate

                // websocket-sharp resets its decompressor every message, so the server must
                // have agreed not to carry compression context across messages.
                if (ext.IndexOf("server_no_context_takeover", StringComparison.Ordinal) < 0)
                    return false;

                foreach (string rawParam in ext.Split(';'))
                {
                    string p = rawParam.Trim();
                    bool ok = p == "permessage-deflate"
                              || p == "server_no_context_takeover"
                              || p == "client_no_context_takeover"
                              || p.StartsWith("server_max_window_bits", StringComparison.Ordinal);
                    if (!ok)
                    {
                        MainMod.Logger.LogWarning($"[AP] Rejecting websocket extension parameter '{p}'.");
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
