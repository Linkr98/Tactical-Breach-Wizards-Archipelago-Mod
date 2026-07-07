using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore;

namespace Tactical_Breach_Wizards_Archipelago_Mod
{
    /// <summary>
    /// The Archipelago logo as an in-game icon. Loads <c>ap_icon.png</c> (shipped next to the
    /// plugin DLL) into a Sprite AND registers it with TextMeshPro under the sprite name
    /// "ap_logo", so both icon paths used by StatusData work:
    ///   * direct <c>Image.sprite</c> (the health/info-panel StatusItem), and
    ///   * inline text via <c>&lt;sprite name="ap_logo"&gt;</c> tags (headers, annotations),
    ///     which TMP only resolves against a registered sprite asset.
    /// The TMP half creates a one-sprite TMP_SpriteAsset at runtime and appends it to
    /// TMP_Settings.defaultSpriteAsset.fallbackSpriteAssets — the sprite-tag search always ends
    /// at the default asset + its fallbacks, so this covers every text field in the game.
    /// </summary>
    public static class ApIcon
    {
        public const string SpriteName = "ap_logo";

        private static bool _attempted;
        private static Sprite _sprite;
        private static bool _tmpRegistered;

        /// <summary>The loaded logo sprite, or null if unavailable.</summary>
        public static Sprite Sprite { get { EnsureLoaded(); return _sprite; } }

        /// <summary>True when the logo is usable EVERYWHERE (sprite loaded AND resolvable in
        /// TMP sprite tags). Callers should fall back to a stock icon when false, because
        /// StatusData.icon feeds both rendering paths from one field.</summary>
        public static bool Ready { get { EnsureLoaded(); return _sprite != null && _tmpRegistered; } }

        private static void EnsureLoaded()
        {
            if (_attempted) return;
            _attempted = true;
            try
            {
                LoadSprite();
                if (_sprite != null) RegisterWithTmp();
            }
            catch (Exception e)
            {
                MainMod.Logger.LogWarning($"[AP] AP logo icon unavailable ({e.Message}); using stock icon.");
            }
        }

        private static void LoadSprite()
        {
            string path = Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "", "ap_icon.png");
            if (!File.Exists(path))
            {
                MainMod.Logger.LogWarning($"[AP] ap_icon.png not found next to the plugin DLL ({path}); using stock icon.");
                return;
            }
            var tex = new Texture2D(2, 2, TextureFormat.ARGB32, mipChain: false)
            {
                name = "ApLogoTexture",
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
            };
            if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(path)))
            {
                MainMod.Logger.LogWarning("[AP] ap_icon.png failed to decode; using stock icon.");
                UnityEngine.Object.Destroy(tex);
                return;
            }
            _sprite = UnityEngine.Sprite.Create(
                tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            _sprite.name = SpriteName;
            _sprite.hideFlags = HideFlags.HideAndDontSave;
            MainMod.Logger.LogInfo($"[AP] Loaded ap_icon.png ({tex.width}x{tex.height}).");
        }

        private static void RegisterWithTmp()
        {
            TMP_SpriteAsset defaultAsset = TMP_Settings.defaultSpriteAsset;
            if (defaultAsset == null)
            {
                MainMod.Logger.LogWarning("[AP] No TMP default sprite asset; AP logo can't be used in text tags.");
                return;
            }

            var tex = (Texture2D)_sprite.texture;
            var asset = ScriptableObject.CreateInstance<TMP_SpriteAsset>();
            asset.name = "AP Runtime Sprites";
            asset.hideFlags = HideFlags.HideAndDontSave;
            // Mark as current-format BEFORE wiring the material: Awake/UpdateLookupTables run
            // UpgradeSpriteAsset() on any asset with a material and an empty version, which
            // rebuilds the tables from the (empty) legacy spriteInfoList — wiping ours.
            AccessTools.Field(typeof(TMP_SpriteAsset), "m_Version").SetValue(asset, "1.1.0");
            asset.spriteSheet = tex;
            var material = new Material(Shader.Find("TextMeshPro/Sprite")) { hideFlags = HideFlags.HideAndDontSave };
            material.SetTexture("_MainTex", tex);
            asset.material = material;

            int w = tex.width, h = tex.height;
            // faceInfo.pointSize stays 0 so TMP uses its legacy sprite path, which scales the
            // glyph so its height matches the surrounding font's ascent — i.e. the 512px logo
            // auto-fits the text. bearingY slightly under the height sits it on the baseline
            // like the game's own status icons.
            var glyph = new TMP_SpriteGlyph(0,
                new GlyphMetrics(w, h, 0f, h * 0.9f, w),
                new GlyphRect(0, 0, w, h), 1f, 0, _sprite);
            asset.spriteGlyphTable.Add(glyph);
            // 0xFFFE = "no unicode": reachable only by name, so it can't shadow any <sprite
            // unicode/index> lookups in the default asset.
            var character = new TMP_SpriteCharacter(0xFFFE, glyph) { name = SpriteName };
            asset.spriteCharacterTable.Add(character);
            asset.UpdateLookupTables();

            if (defaultAsset.fallbackSpriteAssets == null)
                defaultAsset.fallbackSpriteAssets = new List<TMP_SpriteAsset>();
            defaultAsset.fallbackSpriteAssets.Add(asset);
            _tmpRegistered = true;
            MainMod.Logger.LogInfo("[AP] AP logo registered as TMP sprite \"" + SpriteName + "\".");
        }
    }
}
