using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValheimTorchColor
{
    [BepInPlugin("com.yourname.torchcolor", "TorchColor", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        private static readonly Harmony _harmony = new Harmony("com.yourname.torchcolor");

        private void Awake()
        {
            Log = Logger;
            _harmony.PatchAll();
            Log.LogInfo("TorchColor loaded — 'torchcolor <#hex> [brightness]' or 'torchcolor reset'");
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────
    internal static class TorchColors
    {
        public const string KeyHex  = "tc_hex";   // stored as "#RRGGBB"
        public const string KeyMult = "tc_mult";   // brightness multiplier

        // Reflection into LightFlicker
        private static readonly FieldInfo _fBaseIntensity =
            typeof(LightFlicker).GetField("m_baseIntensity",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static readonly FieldInfo _fLight =
            typeof(LightFlicker).GetField("m_light",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        // Per-instance caches keyed by Unity instanceID — always restore from prefab values.
        private static readonly Dictionary<int, float> _origFlicker =
            new Dictionary<int, float>();
        private static readonly Dictionary<int, float> _origLight =
            new Dictionary<int, float>();

        // For gradient-mapped particles (fx_Torch_Basic and similar):
        // cache both CustomData gradient channels so we can restore them exactly.
        private static readonly Dictionary<int, ParticleSystem.MinMaxGradient> _origCustom1 =
            new Dictionary<int, ParticleSystem.MinMaxGradient>();
        private static readonly Dictionary<int, ParticleSystem.MinMaxGradient> _origCustom2 =
            new Dictionary<int, ParticleSystem.MinMaxGradient>();

        // For simpler particles (sparks, glow billboard) that don't use the gradient shader:
        // cache startColor so we can restore those too.
        private static readonly Dictionary<int, ParticleSystem.MinMaxGradient> _origStartColor =
            new Dictionary<int, ParticleSystem.MinMaxGradient>();

        // ── Detection ─────────────────────────────────────────────────────────

        // Returns true when a ParticleSystem uses Valheim's ParticleGradientMapped_Unlit
        // shader approach: CustomDataModule enabled with both Custom1 and Custom2 in Color mode.
        // These are the flame-body particles (fx_Torch_Basic / fx_Torch_Green).
        // Spark and glow particles use simpler materials and fall through to the startColor path.
        private static bool UsesGradientMapping(ParticleSystem ps)
        {
            var cd = ps.customData;
            return cd.enabled
                && cd.GetMode(ParticleSystemCustomData.Custom1) == ParticleSystemCustomDataMode.Color
                && cd.GetMode(ParticleSystemCustomData.Custom2) == ParticleSystemCustomDataMode.Color;
        }

        // ── Cache ──────────────────────────────────────────────────────────────

        // Records the pristine prefab values for all lights and particles under a given root.
        // Works for any networked object: Fireplace-based torches/fires AND always-on lights
        // (dvergr lanterns, lava lanterns) that have no Fireplace component.
        public static void CacheOriginals(Transform root)
        {
            foreach (var flicker in root.GetComponentsInChildren<LightFlicker>(includeInactive: true))
            {
                int id = flicker.GetInstanceID();
                if (!_origFlicker.ContainsKey(id) && _fBaseIntensity != null)
                    _origFlicker[id] = (float)_fBaseIntensity.GetValue(flicker);
            }

            foreach (var light in root.GetComponentsInChildren<Light>(includeInactive: true))
            {
                int id = light.GetInstanceID();
                if (!_origLight.ContainsKey(id))
                    _origLight[id] = light.intensity;
            }

            foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(includeInactive: true))
            {
                int id = ps.GetInstanceID();
                if (UsesGradientMapping(ps))
                {
                    if (!_origCustom1.ContainsKey(id))
                    {
                        var cd = ps.customData;
                        _origCustom1[id] = cd.GetColor(ParticleSystemCustomData.Custom1);
                        _origCustom2[id] = cd.GetColor(ParticleSystemCustomData.Custom2);
                    }
                }
                else
                {
                    if (!_origStartColor.ContainsKey(id))
                        _origStartColor[id] = ps.main.startColor;
                }
            }
        }

        // Fireplace wrapper — delegates to the root-based implementation.
        public static void CacheOriginals(Fireplace fp) => CacheOriginals(fp.transform);

        // ── Apply ──────────────────────────────────────────────────────────────

        // Applies a color and brightness multiplier to all lights and particles under root.
        public static void Apply(Transform root, Color color, float mult)
        {
            var coveredLights = new HashSet<Light>();

            // ── LightFlicker-driven lights ─────────────────────────────────────
            foreach (var flicker in root.GetComponentsInChildren<LightFlicker>(includeInactive: true))
            {
                if (_fBaseIntensity != null)
                {
                    int id     = flicker.GetInstanceID();
                    float orig = _origFlicker.ContainsKey(id)
                        ? _origFlicker[id]
                        : (float)_fBaseIntensity.GetValue(flicker);
                    // 0.35 safety cap: Valheim's bloom saturates to white at full intensity.
                    _fBaseIntensity.SetValue(flicker, orig * mult * 0.35f);
                }

                var light = _fLight != null ? (Light)_fLight.GetValue(flicker) : null;
                if (light == null) continue;
                light.color = color;
                coveredLights.Add(light);
            }

            // ── Lights not driven by LightFlicker ─────────────────────────────
            foreach (var light in root.GetComponentsInChildren<Light>(includeInactive: true))
            {
                if (coveredLights.Contains(light)) continue;
                int id     = light.GetInstanceID();
                float orig = _origLight.ContainsKey(id) ? _origLight[id] : light.intensity;
                light.intensity = orig * mult * 0.35f;
                light.color     = color;
            }

            // ── Particle systems ───────────────────────────────────────────────
            foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(includeInactive: true))
            {
                if (UsesGradientMapping(ps))
                    ApplyGradientMappedFlame(ps, color, mult);
                else
                    ApplySimpleParticle(ps, color);
            }
        }

        // Fireplace wrapper.
        public static void Apply(Fireplace fp, Color color, float mult) => Apply(fp.transform, color, mult);

        // Colours a gradient-mapped flame particle (fx_Torch_Basic / fx_Torch_Green style).
        //
        // Valheim's ParticleGradientMapped_Unlit shader ignores startColor entirely — it reads
        // two HDR colour gradients from Custom1 and Custom2 and uses the greyscale flame texture
        // as a lookup index into those gradients.
        //
        // Custom1 — "cool body" gradient:  bright core → dim edge
        // Custom2 — "hot core" gradient:   bright core → moderate edge
        //
        // Both use HDR (r/g/b > 1 possible) to drive bloom.
        private static void ApplyGradientMappedFlame(ParticleSystem ps, Color color, float mult)
        {
            var cd = ps.customData;
            cd.enabled = true;

            // Custom1 — "cool body": bright at the core, fades to near-black at the edge.
            // Mirrors the structure of the original: key0 = HDR × 2, key1 = dim × 0.25
            var g1 = BuildGradient(color, mult * 2f, mult * 0.25f);
            cd.SetMode(ParticleSystemCustomData.Custom1, ParticleSystemCustomDataMode.Color);
            cd.SetColor(ParticleSystemCustomData.Custom1, new ParticleSystem.MinMaxGradient(g1));

            // Custom2 — "hot core": same HDR peak, but edge stays at 1× brightness
            // (slightly more intense than Custom1's edge, simulating the hotter inner core).
            var g2 = BuildGradient(color, mult * 2f, mult * 1.0f);
            cd.SetMode(ParticleSystemCustomData.Custom2, ParticleSystemCustomDataMode.Color);
            cd.SetColor(ParticleSystemCustomData.Custom2, new ParticleSystem.MinMaxGradient(g2));
        }

        // Tints a simple (non-gradient-mapped) particle system — sparks, glow billboard, etc.
        private static void ApplySimpleParticle(ParticleSystem ps, Color color)
        {
            var main = ps.main;
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(color.r, color.g, color.b, 1f));
        }

        // Builds a two-key Gradient from startColor → endColor derived from the user's hue.
        private static Gradient BuildGradient(Color baseColor, float startBrightness, float endBrightness)
        {
            var g = new Gradient();
            g.SetKeys(
                new[]
                {
                    new GradientColorKey(ScaleColor(baseColor, startBrightness), 0f),
                    new GradientColorKey(ScaleColor(baseColor, endBrightness),   1f),
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, 1f),
                }
            );
            return g;
        }

        // Scales RGB channels independently of alpha (allows HDR values > 1).
        private static Color ScaleColor(Color c, float factor) =>
            new Color(c.r * factor, c.g * factor, c.b * factor, 1f);

        // ── Reset ──────────────────────────────────────────────────────────────

        // Restores an object to its original prefab colours.
        public static void Reset(Transform root)
        {
            foreach (var flicker in root.GetComponentsInChildren<LightFlicker>(includeInactive: true))
            {
                int id = flicker.GetInstanceID();
                if (_fBaseIntensity != null && _origFlicker.ContainsKey(id))
                    _fBaseIntensity.SetValue(flicker, _origFlicker[id]);

                var light = _fLight != null ? (Light)_fLight.GetValue(flicker) : null;
                if (light != null) light.color = Color.white;
            }

            foreach (var light in root.GetComponentsInChildren<Light>(includeInactive: true))
            {
                int id = light.GetInstanceID();
                if (_origLight.ContainsKey(id)) light.intensity = _origLight[id];
                light.color = Color.white;
            }

            foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(includeInactive: true))
            {
                int id = ps.GetInstanceID();
                if (UsesGradientMapping(ps))
                {
                    if (_origCustom1.ContainsKey(id))
                    {
                        var cd = ps.customData;
                        cd.SetColor(ParticleSystemCustomData.Custom1, _origCustom1[id]);
                        cd.SetColor(ParticleSystemCustomData.Custom2, _origCustom2[id]);
                    }
                }
                else
                {
                    if (_origStartColor.ContainsKey(id))
                    {
                        var main = ps.main;
                        main.startColor = _origStartColor[id];
                    }
                }
            }
        }

        // Fireplace wrapper.
        public static void Reset(Fireplace fp) => Reset(fp.transform);

        // ── ZDO helpers ────────────────────────────────────────────────────────

        // Reads color/mult from ZDO and applies it to root. Works for any networked object.
        public static void ApplyFromZDO(Transform root, ZNetView nview)
        {
            if (nview == null || !nview.IsValid()) return;

            var    zdo = nview.GetZDO();
            string hex = zdo.GetString(KeyHex, "");
            if (string.IsNullOrEmpty(hex)) return;

            float mult = zdo.GetFloat(KeyMult, 1f);

            if (ColorUtility.TryParseHtmlString(hex, out Color color))
                Apply(root, color, mult);
        }

        // Fireplace overload (used by existing Fireplace patches).
        public static void ApplyFromZDO(Fireplace fp) =>
            ApplyFromZDO(fp.transform, fp.GetComponent<ZNetView>());
    }

    // ── Patches ────────────────────────────────────────────────────────────────

    // Cache pristine intensities and apply any stored color right when the torch loads.
    [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.Awake))]
    public static class FireplaceAwakePatch
    {
        static void Postfix(Fireplace __instance)
        {
            TorchColors.CacheOriginals(__instance.transform);
            TorchColors.ApplyFromZDO(__instance);
        }
    }

    // Re-apply after the fuel state changes (switches between high/low/off GameObjects).
    [HarmonyPatch(typeof(Fireplace), "UpdateState")]
    public static class FireplaceUpdateStatePatch
    {
        static void Postfix(Fireplace __instance)
        {
            TorchColors.ApplyFromZDO(__instance);
        }
    }

    // Apply saved color to always-on lights (dvergr lanterns, lava lanterns, etc.) that
    // have a Piece + ZNetView but no Fireplace component.
    // Hooks ZNetView.Awake because Piece does not define its own Awake override.
    [HarmonyPatch(typeof(ZNetView), "Awake")]
    public static class NonFireplaceLightAwakePatch
    {
        static void Postfix(ZNetView __instance)
        {
            // Only handle placed buildable pieces.
            if (__instance.GetComponent<Piece>() == null) return;
            // Fireplace objects are fully handled by FireplaceAwakePatch / FireplaceUpdateStatePatch.
            if (__instance.GetComponent<Fireplace>() != null) return;
            TorchColors.CacheOriginals(__instance.transform);
            TorchColors.ApplyFromZDO(__instance.transform, __instance);
        }
    }

    // ── Console commands ───────────────────────────────────────────────────────
    [HarmonyPatch(typeof(Terminal), nameof(Terminal.Awake))]
    public static class TerminalPatch
    {
        private static bool _registered;

        static void Postfix()
        {
            if (_registered) return;
            _registered = true;

            new Terminal.ConsoleCommand(
                "torchcolor",
                "<#RRGGBB | reset> [brightness] — Set color and brightness of the torch you're looking at (max 10 m)",
                args =>
                {
                    if (args.Length < 2)
                    {
                        args.Context.AddString("Usage: torchcolor <#RRGGBB | reset> [brightness]");
                        args.Context.AddString("  Examples:  torchcolor #FF4400 1.5");
                        args.Context.AddString("             torchcolor #0088FF       (no brightness = keep default)");
                        args.Context.AddString("             torchcolor reset");
                        return;
                    }

                    // ── Find the light source the player is looking at ──
                    // Works for Fireplace-driven objects (torches, fire pits) AND always-on
                    // lights (dvergr lanterns, lava lanterns) — any ZNetView-bearing piece.
                    var player = Player.m_localPlayer;
                    if (player == null) { args.Context.AddString("No local player found."); return; }

                    var hoverObj = player.GetHoverObject();
                    ZNetView nview = null;

                    if (hoverObj != null)
                        nview = hoverObj.GetComponentInParent<ZNetView>();

                    if (nview == null)
                    {
                        // Fallback: broad raycast so the user gets a useful debug line.
                        var cam = GameCamera.instance != null
                            ? (Camera)typeof(GameCamera)
                                .GetField("m_camera", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                                .GetValue(GameCamera.instance)
                            : Camera.main;

                        string hitName = "nothing";
                        if (cam != null && Physics.Raycast(cam.transform.position,
                                cam.transform.forward, out RaycastHit hit, 10f))
                        {
                            hitName = hit.collider.gameObject.name;
                            nview = hit.collider.GetComponentInParent<ZNetView>();
                        }

                        if (nview == null)
                        {
                            args.Context.AddString(
                                $"Not looking at a light source (hover='{hoverObj?.name ?? "none"}', raycast='{hitName}'). Aim at a torch, fire pit, or lantern.");
                            return;
                        }
                    }

                    if (!nview.IsValid())
                    {
                        args.Context.AddString("Object has no valid network view — cannot save.");
                        return;
                    }

                    var root        = nview.transform;
                    var zdo         = nview.GetZDO();
                    var displayName = nview.GetComponent<Fireplace>()?.m_name
                        ?? nview.GetComponent<Piece>()?.m_name
                        ?? root.name;

                    // Ensure originals are cached before any modification or reset.
                    TorchColors.CacheOriginals(root);

                    // ── Reset ──
                    if (args[1].ToLowerInvariant() == "reset")
                    {
                        zdo.Set(TorchColors.KeyHex, "");
                        zdo.Set(TorchColors.KeyMult, 1f);
                        TorchColors.Reset(root);
                        args.Context.AddString($"'{displayName}' reset to default.");
                        return;
                    }

                    // ── Parse hex color ──
                    string hexInput = args[1].StartsWith("#") ? args[1] : "#" + args[1];
                    if (!ColorUtility.TryParseHtmlString(hexInput, out Color color))
                    {
                        args.Context.AddString($"Invalid color '{args[1]}'. Use a 6-digit hex like #FF4400 or FF4400.");
                        return;
                    }

                    // ── Parse brightness multiplier ──
                    float mult = 1f;
                    if (args.Length > 2 && float.TryParse(args[2], out float parsed))
                        mult = Mathf.Clamp(parsed, 0.01f, 10f);

                    // ── Save to ZDO (persists across sessions and reloads) ──
                    zdo.Set(TorchColors.KeyHex,  hexInput);
                    zdo.Set(TorchColors.KeyMult, mult);

                    // ── Apply immediately ──
                    TorchColors.Apply(root, color, mult);

                    args.Context.AddString(
                        $"'{displayName}' → color={hexInput}  brightness×{mult:F2}");
                },
                isCheat: true
            );
        }
    }
}
