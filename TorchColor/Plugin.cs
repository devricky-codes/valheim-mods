using System.Collections;
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
        private Coroutine _reapplyLoop;

        private void Awake()
        {
            Log = Logger;
            _harmony.PatchAll();
            _reapplyLoop = StartCoroutine(PeriodicReapplyLoop());
            Log.LogInfo("TorchColor loaded — 'torchcolor <#hex> [brightness]' or 'torchcolor reset'");
        }

        private IEnumerator PeriodicReapplyLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(20f);

                // Only sweep while actually in-game.
                if (Player.m_localPlayer == null) continue;

                TorchColors.ReapplyAllLoadedFromZDO();
            }
        }

        private void OnDestroy()
        {
            if (_reapplyLoop != null)
            {
                StopCoroutine(_reapplyLoop);
                _reapplyLoop = null;
            }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────
    internal static class TorchColors
    {
        public const string KeyHex  = "tc_hex";   // stored as "#RRGGBB"
        public const string KeyMult = "tc_mult";   // brightness multiplier

        // Snapshot keys: capture command-time visual state and restore on reload.
        public const string KeySnapVer            = "tc_sv";
        public const string KeySnapBaseIntensity  = "tc_s_base";
        public const string KeySnapLightIntensity = "tc_s_li";
        public const string KeySnapLightRange     = "tc_s_lr";
        public const string KeySnapFlareSize      = "tc_s_fs";
        public const string KeySnapFlareScaleX    = "tc_s_fsx";
        public const string KeySnapFlareScaleY    = "tc_s_fsy";
        public const string KeySnapFlareScaleZ    = "tc_s_fsz";
        public const string KeySnapFlareTintR     = "tc_s_ftr";
        public const string KeySnapFlareTintG     = "tc_s_ftg";
        public const string KeySnapFlareTintB     = "tc_s_ftb";
        public const string KeySnapFlareTintA     = "tc_s_fta";

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

        // Some glow sprites are colorized primarily via material properties rather than
        // particle startColor/custom-data. Cache those so reset is exact.
        private static readonly Dictionary<int, Color> _origMatColor =
            new Dictionary<int, Color>();
        private static readonly Dictionary<int, Color> _origMatEmission =
            new Dictionary<int, Color>();
        private static readonly Dictionary<int, Color> _origMatTint =
            new Dictionary<int, Color>();
        private static readonly Dictionary<int, Color> _origMatBase =
            new Dictionary<int, Color>();

        private static readonly Dictionary<int, Color> _origSpriteColor =
            new Dictionary<int, Color>();
        private static readonly Dictionary<int, Color> _origLensFlareColor =
            new Dictionary<int, Color>();

        // ── Detection ─────────────────────────────────────────────────────────

        // Returns true when a ParticleSystem uses Valheim's gradient-mapped custom-data path.
        // Some particles use both channels, others only one; treat either as gradient-mapped.
        private static bool UsesGradientMapping(ParticleSystem ps)
        {
            var cd = ps.customData;
            return cd.enabled
                && (
                    cd.GetMode(ParticleSystemCustomData.Custom1) == ParticleSystemCustomDataMode.Color
                    || cd.GetMode(ParticleSystemCustomData.Custom2) == ParticleSystemCustomDataMode.Color
                );
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
                    var cd = ps.customData;
                    bool c1 = cd.GetMode(ParticleSystemCustomData.Custom1) == ParticleSystemCustomDataMode.Color;
                    bool c2 = cd.GetMode(ParticleSystemCustomData.Custom2) == ParticleSystemCustomDataMode.Color;

                    if (c1 && !_origCustom1.ContainsKey(id))
                        _origCustom1[id] = cd.GetColor(ParticleSystemCustomData.Custom1);
                    if (c2 && !_origCustom2.ContainsKey(id))
                        _origCustom2[id] = cd.GetColor(ParticleSystemCustomData.Custom2);
                }
                else
                {
                    if (!_origStartColor.ContainsKey(id))
                        _origStartColor[id] = ps.main.startColor;
                }

                var psr = ps.GetComponent<ParticleSystemRenderer>();
                if (psr == null) continue;
                CacheRendererMaterialColors(psr);
            }

            // Extra glow pass: cache renderer/sprite/lens-flare colors that can drive
            // the large round glow orb separately from particle startColor.
            foreach (var r in root.GetComponentsInChildren<Renderer>(includeInactive: true))
                if (ShouldTintRenderer(r))
                    CacheRendererMaterialColors(r);

            foreach (var sr in root.GetComponentsInChildren<SpriteRenderer>(includeInactive: true))
            {
                int id = sr.GetInstanceID();
                if (!_origSpriteColor.ContainsKey(id))
                    _origSpriteColor[id] = sr.color;
            }

            foreach (var lf in root.GetComponentsInChildren<LensFlare>(includeInactive: true))
            {
                int id = lf.GetInstanceID();
                if (!_origLensFlareColor.ContainsKey(id))
                    _origLensFlareColor[id] = lf.color;
            }
        }

        private static bool ShouldTintRenderer(Renderer r)
        {
            if (r == null) return false;
            if (r is ParticleSystemRenderer || r is SpriteRenderer) return true;

            string go  = r.gameObject.name.ToLowerInvariant();
            string mat = r.sharedMaterial != null ? r.sharedMaterial.name.ToLowerInvariant() : "";
            string all = go + " " + mat;

            return all.Contains("flare")
                || all.Contains("glow")
                || all.Contains("light")
                || all.Contains("fx")
                || all.Contains("sprite")
                || all.Contains("billboard");
        }

        private static void CacheRendererMaterialColors(Renderer renderer)
        {
            if (renderer == null) return;

            int rid = renderer.GetInstanceID();
            var mat = renderer.sharedMaterial;
            if (mat == null) return;

            if (mat.HasProperty("_Color") && !_origMatColor.ContainsKey(rid))
                _origMatColor[rid] = mat.GetColor("_Color");

            if (mat.HasProperty("_EmissionColor") && !_origMatEmission.ContainsKey(rid))
                _origMatEmission[rid] = mat.GetColor("_EmissionColor");

            if (mat.HasProperty("_TintColor") && !_origMatTint.ContainsKey(rid))
                _origMatTint[rid] = mat.GetColor("_TintColor");

            if (mat.HasProperty("_BaseColor") && !_origMatBase.ContainsKey(rid))
                _origMatBase[rid] = mat.GetColor("_BaseColor");
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
                float target = 0f;
                if (_fBaseIntensity != null)
                {
                    int id     = flicker.GetInstanceID();
                    float orig = _origFlicker.ContainsKey(id)
                        ? _origFlicker[id]
                        : (float)_fBaseIntensity.GetValue(flicker);
                    // 0.35 safety cap: Valheim's bloom saturates to white at full intensity.
                    target = orig * mult * 0.35f;
                    _fBaseIntensity.SetValue(flicker, target);
                }

                var light = _fLight != null ? (Light)_fLight.GetValue(flicker) : null;
                if (light == null) continue;
                light.enabled = true;
                light.color = color;
                if (_fBaseIntensity != null)
                    light.intensity = target;
                coveredLights.Add(light);
            }

            // ── Lights not driven by LightFlicker ─────────────────────────────
            foreach (var light in root.GetComponentsInChildren<Light>(includeInactive: true))
            {
                if (coveredLights.Contains(light)) continue;
                int id     = light.GetInstanceID();
                float orig = _origLight.ContainsKey(id) ? _origLight[id] : light.intensity;

                // If this light belongs to a LightFlicker but that flicker's m_light
                // wasn't available yet, use the flicker baseline (m_baseIntensity)
                // instead of light.intensity baseline to avoid oversized halo.
                var lf = light.GetComponent<LightFlicker>();
                if (lf != null)
                {
                    int lfId = lf.GetInstanceID();
                    if (_origFlicker.TryGetValue(lfId, out float fOrig))
                        orig = fOrig;
                }

                light.enabled = true;
                light.intensity = orig * mult * 0.35f;
                light.color     = color;
            }

            // ── Particle systems ───────────────────────────────────────────────
            foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(includeInactive: true))
            {
                // Keep simple paths colored even when a shader ignores one source.
                ApplySimpleParticle(ps, color);
                ApplyParticleMaterialTint(ps, color, mult);

                if (UsesGradientMapping(ps))
                    ApplyGradientMappedFlame(ps, color, mult);
            }

            // Some torch glow visuals are renderer/lensflare driven, not particle-color driven.
            ApplyExtraGlowVisuals(root, color, mult);
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

            bool c1 = cd.GetMode(ParticleSystemCustomData.Custom1) == ParticleSystemCustomDataMode.Color;
            bool c2 = cd.GetMode(ParticleSystemCustomData.Custom2) == ParticleSystemCustomDataMode.Color;

            if (!c1 && !c2) return;

            // Custom1 — "cool body": bright at the core, fades to near-black at the edge.
            // Mirrors the structure of the original: key0 = HDR × 2, key1 = dim × 0.25
            var g1 = BuildGradient(color, mult * 2f, mult * 0.25f);
            if (c1)
            {
                cd.SetMode(ParticleSystemCustomData.Custom1, ParticleSystemCustomDataMode.Color);
                cd.SetColor(ParticleSystemCustomData.Custom1, new ParticleSystem.MinMaxGradient(g1));
            }

            // Custom2 — "hot core": same HDR peak, but edge stays at 1× brightness
            // (slightly more intense than Custom1's edge, simulating the hotter inner core).
            var g2 = BuildGradient(color, mult * 2f, mult * 1.0f);
            if (c2)
            {
                cd.SetMode(ParticleSystemCustomData.Custom2, ParticleSystemCustomDataMode.Color);
                cd.SetColor(ParticleSystemCustomData.Custom2, new ParticleSystem.MinMaxGradient(g2));
            }
        }

        // Tints a simple (non-gradient-mapped) particle system — sparks, glow billboard, etc.
        private static void ApplySimpleParticle(ParticleSystem ps, Color color)
        {
            var main = ps.main;

            // Preserve the prefab alpha for billboard glows like "flare".
            // Forcing alpha=1 turns the soft halo into a solid white orb.
            float alpha = main.startColor.color.a;
            if (_origStartColor.TryGetValue(ps.GetInstanceID(), out var orig))
                alpha = orig.color.a;

            main.startColor = new ParticleSystem.MinMaxGradient(new Color(color.r, color.g, color.b, alpha));
        }

        // Tints particle material channels that can drive glow color in additive billboard effects.
        private static void ApplyParticleMaterialTint(ParticleSystem ps, Color color, float mult)
        {
            var psr = ps.GetComponent<ParticleSystemRenderer>();
            if (psr == null) return;

            TintRendererMaterial(psr, color, mult);
        }

        private static void ApplyExtraGlowVisuals(Transform root, Color color, float mult)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(includeInactive: true))
            {
                if (!ShouldTintRenderer(r)) continue;
                TintRendererMaterial(r, color, mult);
            }

            foreach (var sr in root.GetComponentsInChildren<SpriteRenderer>(includeInactive: true))
                sr.color = new Color(color.r, color.g, color.b, sr.color.a);

            foreach (var lf in root.GetComponentsInChildren<LensFlare>(includeInactive: true))
                lf.color = color;
        }

        private static void TintRendererMaterial(Renderer renderer, Color color, float mult)
        {
            if (renderer == null) return;
            var mat = renderer.material;
            if (mat == null) return;

            int rid = renderer.GetInstanceID();

            if (mat.HasProperty("_Color"))
            {
                Color baseC = _origMatColor.TryGetValue(rid, out Color c) ? c : mat.GetColor("_Color");
                mat.SetColor("_Color", RehuePreserveIntensity(baseC, color, keepAlpha: true));
            }
            if (mat.HasProperty("_TintColor"))
            {
                Color baseT = _origMatTint.TryGetValue(rid, out Color t) ? t : mat.GetColor("_TintColor");
                mat.SetColor("_TintColor", RehuePreserveIntensity(baseT, color, keepAlpha: true));
            }
            if (mat.HasProperty("_BaseColor"))
            {
                Color baseB = _origMatBase.TryGetValue(rid, out Color b) ? b : mat.GetColor("_BaseColor");
                mat.SetColor("_BaseColor", RehuePreserveIntensity(baseB, color, keepAlpha: true));
            }
            if (mat.HasProperty("_EmissionColor"))
            {
                // Preserve original emission magnitude so saved/legacy torches do not
                // explode into oversized bloom circles after reload.
                Color baseE = _origMatEmission.TryGetValue(rid, out Color e) ? e : mat.GetColor("_EmissionColor");
                mat.SetColor("_EmissionColor", RehuePreserveIntensity(baseE, color, keepAlpha: false));
            }
        }

        private static Color RehuePreserveIntensity(Color original, Color targetHue, bool keepAlpha)
        {
            float intensity = Mathf.Max(original.r, Mathf.Max(original.g, original.b));
            if (intensity <= 0.0001f) intensity = 1f;
            return new Color(
                targetHue.r * intensity,
                targetHue.g * intensity,
                targetHue.b * intensity,
                keepAlpha ? original.a : 1f
            );
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

        // ── Diagnostics ───────────────────────────────────────────────────────────

        public static void Diagnose(ZNetView nview, string tag = "manual")
        {
            if (nview == null) return;
            var root = nview.transform;
            var log  = Plugin.Log;

            var zdo = nview.IsValid() ? nview.GetZDO() : null;
            string hex  = zdo != null ? zdo.GetString(KeyHex, "(none)") : "(no zdo)";
            float  mult = zdo != null ? zdo.GetFloat(KeyMult, 1f) : 1f;
            var piece = nview.GetComponent<Piece>();
            var fp    = nview.GetComponent<Fireplace>();
            bool owner = nview.IsValid() && nview.IsOwner();
            int rootId = root.GetInstanceID();
            int nviewId = nview.GetInstanceID();

            log.LogInfo(
                $"[TorchDiag] === tag={tag} frame={Time.frameCount} time={Time.time:F3} root='{root.name}' rootId={rootId} nviewId={nviewId} path='{GetPath(root)}' ===");
            log.LogInfo(
                $"[TorchDiag]   nview valid={nview.IsValid()} owner={owner} active={root.gameObject.activeInHierarchy} piece='{piece?.m_name ?? "(none)"}' fireplace={(fp != null)} pos={root.position}");
            log.LogInfo(
                $"[TorchDiag]   ZDO hex={hex} mult={mult:F3} cacheCounts flicker={_origFlicker.Count} light={_origLight.Count} c1={_origCustom1.Count} c2={_origCustom2.Count} start={_origStartColor.Count} matTint={_origMatTint.Count}");
            LogNearbyFireplaces(root, log);
            LogNearbyNonChildLights(root, log);
            LogCustomComponents(root, log);

            log.LogInfo("[TorchDiag] --- Lights ---");
            foreach (var l in root.GetComponentsInChildren<Light>(includeInactive: true))
            {
                int id = l.GetInstanceID();
                string origStr = _origLight.TryGetValue(id, out float ol) ? ol.ToString("F4") : "?";
                var lf = l.GetComponent<LightFlicker>();
                string lfStr = lf != null ? $" lfId={lf.GetInstanceID()}" : "";
                bool lodGroup = l.GetComponentInParent<LODGroup>() != null;
                var lightLodComp = FindLightLodComponent(l.transform);
                bool lightLod = lightLodComp != null;
                string lodFields = lightLod ? SummarizeSimpleFields(lightLodComp, 8) : "";
                log.LogInfo($"[TorchDiag]   '{l.gameObject.name}' id={id}{lfStr} enabled={l.enabled} active={l.gameObject.activeInHierarchy} lodGroup={lodGroup} lightLod={lightLod} intensity={l.intensity:F4} range={l.range:F2} color={l.color} origLight={origStr}");
                if (lightLod)
                    log.LogInfo($"[TorchDiag]     LightLod type={lightLodComp.GetType().FullName} fields={lodFields}");
            }

            log.LogInfo("[TorchDiag] --- LightFlicker ---");
            foreach (var lf in root.GetComponentsInChildren<LightFlicker>(includeInactive: true))
            {
                int id = lf.GetInstanceID();
                float baseI = _fBaseIntensity != null ? (float)_fBaseIntensity.GetValue(lf) : -1f;
                string origStr = _origFlicker.TryGetValue(id, out float of2) ? of2.ToString("F4") : "?";
                var linked = _fLight != null ? (Light)_fLight.GetValue(lf) : null;
                log.LogInfo($"[TorchDiag]   '{lf.gameObject.name}' id={id} enabled={lf.enabled} active={lf.gameObject.activeInHierarchy} m_baseIntensity={baseI:F4} origFlicker={origStr} linkedLight={(linked != null ? linked.GetInstanceID().ToString() : "null")}");
            }

            log.LogInfo("[TorchDiag] --- Renderers (all; ShouldTint flagged) ---");
            foreach (var r in root.GetComponentsInChildren<Renderer>(includeInactive: true))
            {
                bool tint = ShouldTintRenderer(r);
                var  smat = r.sharedMaterial;
                var  mat  = r.material;
                int  rid  = r.GetInstanceID();
                if (mat == null)
                {
                    log.LogInfo($"[TorchDiag]   '{r.gameObject.name}' [{r.GetType().Name}] shouldTint={tint}  mat=null");
                    continue;
                }
                string ec    = mat.HasProperty("_EmissionColor") ? mat.GetColor("_EmissionColor").ToString() : "n/a";
                string tc    = mat.HasProperty("_TintColor")     ? mat.GetColor("_TintColor").ToString()     : "n/a";
                string col   = mat.HasProperty("_Color")         ? mat.GetColor("_Color").ToString()         : "n/a";
                string origE = _origMatEmission.TryGetValue(rid, out Color oe) ? oe.ToString() : "?";
                string origT = _origMatTint.TryGetValue(rid, out Color ot)     ? ot.ToString() : "?";
                string origC = _origMatColor.TryGetValue(rid, out Color oc)    ? oc.ToString() : "?";
                log.LogInfo($"[TorchDiag]   '{r.gameObject.name}' [{r.GetType().Name}] shouldTint={tint} enabled={r.enabled} active={r.gameObject.activeInHierarchy} scale={r.transform.localScale} sharedMat='{(smat != null ? smat.name : "null")}' runtimeMat='{mat.name}' shader='{mat.shader?.name}'");
                log.LogInfo($"[TorchDiag]     live  _Color={col}  _TintColor={tc}  _EmissionColor={ec}");
                log.LogInfo($"[TorchDiag]     cache origC={origC}  origT={origT}  origE={origE}");
            }

            log.LogInfo("[TorchDiag] --- ParticleSystems ---");
            foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(includeInactive: true))
            {
                bool grad = UsesGradientMapping(ps);
                var  main = ps.main;
                var startSize = main.startSize;

                int cap = Mathf.Clamp(main.maxParticles, 1, 128);
                var buffer = new ParticleSystem.Particle[cap];
                int alive = ps.GetParticles(buffer);
                float avgSize = 0f;
                float maxSize = 0f;
                for (int i = 0; i < alive; i++)
                {
                    float s = buffer[i].GetCurrentSize(ps);
                    avgSize += s;
                    if (s > maxSize) maxSize = s;
                }
                if (alive > 0) avgSize /= alive;

                log.LogInfo(
                    $"[TorchDiag]   '{ps.gameObject.name}' enabled={ps.gameObject.activeInHierarchy} usesGradient={grad} startColor={main.startColor.color} maxParticles={main.maxParticles} simSpeed={main.simulationSpeed:F2} startSizeMode={startSize.mode} startSizeConst={startSize.constant:F3} startSizeMin={startSize.constantMin:F3} startSizeMax={startSize.constantMax:F3} alive={alive} avgSize={avgSize:F3} maxSize={maxSize:F3}");
            }

            log.LogInfo("[TorchDiag] --- LensFlares ---");
            foreach (var lfl in root.GetComponentsInChildren<LensFlare>(includeInactive: true))
                log.LogInfo($"[TorchDiag]   '{lfl.gameObject.name}'  color={lfl.color}  brightness={lfl.brightness:F4}");
        }

        private static string GetPath(Transform t)
        {
            if (t == null) return "(null)";
            string path = t.name;
            var p = t.parent;
            while (p != null)
            {
                path = p.name + "/" + path;
                p = p.parent;
            }
            return path;
        }

        private static Component FindLightLodComponent(Transform t)
        {
            if (t == null) return null;
            foreach (var mb in t.GetComponentsInParent<MonoBehaviour>(includeInactive: true))
            {
                if (mb != null && mb.GetType().Name == "LightLod")
                    return mb;
            }
            return null;
        }

        private static string SummarizeSimpleFields(Component comp, int maxFields)
        {
            if (comp == null) return "";

            var fields = comp.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var parts = new List<string>();

            foreach (var f in fields)
            {
                if (f.IsStatic) continue;

                var ft = f.FieldType;
                bool simple = ft == typeof(float)
                    || ft == typeof(int)
                    || ft == typeof(bool)
                    || ft == typeof(string)
                    || ft == typeof(Vector3);
                if (!simple) continue;

                object value = f.GetValue(comp);
                parts.Add(f.Name + "=" + (value != null ? value.ToString() : "null"));
                if (parts.Count >= maxFields) break;
            }

            return string.Join(", ", parts.ToArray());
        }

        private static void LogNearbyFireplaces(Transform root, ManualLogSource log)
        {
            const float radius = 2f;
            var nearby = new List<Fireplace>();

            foreach (var fp in Object.FindObjectsOfType<Fireplace>())
            {
                if (fp == null) continue;
                if (Vector3.Distance(fp.transform.position, root.position) <= radius)
                    nearby.Add(fp);
            }

            log.LogInfo($"[TorchDiag] --- Nearby Fireplaces <= {radius:F1}m : {nearby.Count} ---");
            for (int i = 0; i < nearby.Count && i < 8; i++)
            {
                var fp = nearby[i];
                var nv = fp.GetComponent<ZNetView>();
                var zdo = nv != null && nv.IsValid() ? nv.GetZDO() : null;
                string h = zdo != null ? zdo.GetString(KeyHex, "") : "";
                float m = zdo != null ? zdo.GetFloat(KeyMult, 1f) : 1f;
                float d = Vector3.Distance(fp.transform.position, root.position);
                log.LogInfo($"[TorchDiag]   nearby#{i} name='{fp.name}' id={fp.GetInstanceID()} dist={d:F3} pos={fp.transform.position} hex='{h}' mult={m:F3}");
            }
        }

        private static void LogNearbyNonChildLights(Transform root, ManualLogSource log)
        {
            const float radius = 8f;
            var allLights = Object.FindObjectsOfType<Light>();
            var list = new List<Light>();

            foreach (var l in allLights)
            {
                if (l == null) continue;
                if (!l.gameObject.activeInHierarchy) continue;
                if (l.transform.IsChildOf(root)) continue;

                float d = Vector3.Distance(l.transform.position, root.position);
                if (d <= radius)
                    list.Add(l);
            }

            log.LogInfo($"[TorchDiag] --- Nearby Non-Child Lights <= {radius:F1}m : {list.Count} ---");
            for (int i = 0; i < list.Count && i < 16; i++)
            {
                var l = list[i];
                float d = Vector3.Distance(l.transform.position, root.position);
                var lf = l.GetComponent<LightFlicker>();
                log.LogInfo($"[TorchDiag]   extLight#{i} name='{l.name}' id={l.GetInstanceID()} dist={d:F3} pos={l.transform.position} intensity={l.intensity:F4} range={l.range:F2} color={l.color} flicker={(lf != null ? lf.GetInstanceID().ToString() : "none")} path='{GetPath(l.transform)}'");
            }
        }

        private static void LogCustomComponents(Transform root, ManualLogSource log)
        {
            var seen = new HashSet<string>();
            var custom = new List<Component>();

            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(includeInactive: true))
            {
                if (mb == null) continue;

                string asm = mb.GetType().Assembly.GetName().Name;
                string full = mb.GetType().FullName;

                // Keep vanilla game scripts out; focus on mod-added behaviors.
                if (asm == "Assembly-CSharp" || asm.StartsWith("Unity") || asm.StartsWith("BepInEx") || asm.StartsWith("Harmony"))
                    continue;

                string key = asm + "::" + full + "@" + mb.GetInstanceID();
                if (seen.Contains(key)) continue;
                seen.Add(key);
                custom.Add(mb);
            }

            log.LogInfo($"[TorchDiag] --- Custom Components Under Torch : {custom.Count} ---");
            for (int i = 0; i < custom.Count && i < 24; i++)
            {
                var c = custom[i];
                string asm = c.GetType().Assembly.GetName().Name;
                string full = c.GetType().FullName;
                log.LogInfo($"[TorchDiag]   custom#{i} asm={asm} type={full} go='{c.gameObject.name}' path='{GetPath(c.transform)}' fields={SummarizeSimpleFields(c, 10)}");
            }
        }

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
                    var cd = ps.customData;
                    if (_origCustom1.ContainsKey(id))
                    {
                        cd.SetColor(ParticleSystemCustomData.Custom1, _origCustom1[id]);
                    }
                    if (_origCustom2.ContainsKey(id))
                    {
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

                ResetParticleMaterialTint(ps);
            }

            ResetExtraGlowVisuals(root);
        }

        private static void ResetParticleMaterialTint(ParticleSystem ps)
        {
            var psr = ps.GetComponent<ParticleSystemRenderer>();
            if (psr == null) return;

            ResetRendererMaterialTint(psr);
        }

        private static void ResetExtraGlowVisuals(Transform root)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(includeInactive: true))
            {
                if (!ShouldTintRenderer(r)) continue;
                ResetRendererMaterialTint(r);
            }

            foreach (var sr in root.GetComponentsInChildren<SpriteRenderer>(includeInactive: true))
            {
                int id = sr.GetInstanceID();
                if (_origSpriteColor.TryGetValue(id, out Color c))
                    sr.color = c;
            }

            foreach (var lf in root.GetComponentsInChildren<LensFlare>(includeInactive: true))
            {
                int id = lf.GetInstanceID();
                if (_origLensFlareColor.TryGetValue(id, out Color c))
                    lf.color = c;
            }
        }

        private static void ResetRendererMaterialTint(Renderer renderer)
        {
            if (renderer == null) return;
            var mat = renderer.material;
            if (mat == null) return;

            int rid = renderer.GetInstanceID();
            if (mat.HasProperty("_Color") && _origMatColor.TryGetValue(rid, out Color c))
                mat.SetColor("_Color", c);
            if (mat.HasProperty("_TintColor") && _origMatTint.TryGetValue(rid, out Color t))
                mat.SetColor("_TintColor", t);
            if (mat.HasProperty("_BaseColor") && _origMatBase.TryGetValue(rid, out Color b))
                mat.SetColor("_BaseColor", b);
            if (mat.HasProperty("_EmissionColor") && _origMatEmission.TryGetValue(rid, out Color e))
                mat.SetColor("_EmissionColor", e);
        }

        // Fireplace wrapper.
        public static void Reset(Fireplace fp) => Reset(fp.transform);

        // ── ZDO helpers ────────────────────────────────────────────────────────

        // Reads color/mult from ZDO and applies it to root. Works for any networked object.
        public static void ApplyFromZDO(Transform root, ZNetView nview)
        {
            if (nview == null || !nview.IsValid()) return;

            EnsureRuntimeEnforcer(root);

            // Important ordering guarantee: always cache pristine values before any
            // apply path (Awake/UpdateState/deferred) so legacy saved torches cannot
            // accidentally treat previously modified glow values as "original".
            CacheOriginals(root);

            var    zdo = nview.GetZDO();
            string hex = zdo.GetString(KeyHex, "");
            if (string.IsNullOrEmpty(hex)) return;

            float mult = zdo.GetFloat(KeyMult, 1f);

            if (ColorUtility.TryParseHtmlString(hex, out Color color))
            {
                Apply(root, color, mult);

                // If this torch was explicitly customized earlier, replay its
                // captured command-time visual snapshot for exact consistency.
                ApplySnapshot(root, nview);
            }
        }

        // Fireplace overload (used by existing Fireplace patches).
        public static void ApplyFromZDO(Fireplace fp) =>
            ApplyFromZDO(fp.transform, fp.GetComponent<ZNetView>());

        public static void EnsureRuntimeEnforcer(Transform root)
        {
            if (root == null) return;
            if (root.GetComponent<TorchColorRuntimeEnforcer>() != null) return;
            root.gameObject.AddComponent<TorchColorRuntimeEnforcer>();
        }

        public static void ClearSnapshot(ZNetView nview)
        {
            if (nview == null || !nview.IsValid()) return;
            var zdo = nview.GetZDO();

            zdo.Set(KeySnapVer, 0f);
            zdo.Set(KeySnapBaseIntensity, 0f);
            zdo.Set(KeySnapLightIntensity, 0f);
            zdo.Set(KeySnapLightRange, 0f);
            zdo.Set(KeySnapFlareSize, 0f);
            zdo.Set(KeySnapFlareScaleX, 0f);
            zdo.Set(KeySnapFlareScaleY, 0f);
            zdo.Set(KeySnapFlareScaleZ, 0f);
            zdo.Set(KeySnapFlareTintR, 0f);
            zdo.Set(KeySnapFlareTintG, 0f);
            zdo.Set(KeySnapFlareTintB, 0f);
            zdo.Set(KeySnapFlareTintA, 0f);
        }

        public static void CaptureSnapshot(Transform root, ZNetView nview)
        {
            if (root == null || nview == null || !nview.IsValid()) return;
            var zdo = nview.GetZDO();

            var flicker = root.GetComponentInChildren<LightFlicker>(includeInactive: true);
            if (flicker != null && _fBaseIntensity != null)
            {
                float baseI = (float)_fBaseIntensity.GetValue(flicker);
                zdo.Set(KeySnapBaseIntensity, baseI);
            }

            var light = root.GetComponentInChildren<Light>(includeInactive: true);
            if (light != null)
            {
                zdo.Set(KeySnapLightIntensity, light.intensity);
                zdo.Set(KeySnapLightRange, light.range);
            }

            ParticleSystem flare = null;
            foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(includeInactive: true))
            {
                string n = ps.gameObject.name.ToLowerInvariant();
                if (n.Contains("flare"))
                {
                    flare = ps;
                    break;
                }
            }

            if (flare != null)
            {
                var main = flare.main;
                zdo.Set(KeySnapFlareSize, main.startSize.constant);

                var s = flare.transform.localScale;
                zdo.Set(KeySnapFlareScaleX, s.x);
                zdo.Set(KeySnapFlareScaleY, s.y);
                zdo.Set(KeySnapFlareScaleZ, s.z);

                var psr = flare.GetComponent<ParticleSystemRenderer>();
                if (psr != null)
                {
                    var mat = psr.material;
                    if (mat != null && mat.HasProperty("_TintColor"))
                    {
                        Color t = mat.GetColor("_TintColor");
                        zdo.Set(KeySnapFlareTintR, t.r);
                        zdo.Set(KeySnapFlareTintG, t.g);
                        zdo.Set(KeySnapFlareTintB, t.b);
                        zdo.Set(KeySnapFlareTintA, t.a);
                    }
                }
            }

            zdo.Set(KeySnapVer, 1f);
        }

        public static void ApplySnapshot(Transform root, ZNetView nview)
        {
            if (root == null || nview == null || !nview.IsValid()) return;
            var zdo = nview.GetZDO();
            if (zdo.GetFloat(KeySnapVer, 0f) <= 0f) return;

            float snapBase = zdo.GetFloat(KeySnapBaseIntensity, 0f);
            float snapLI   = zdo.GetFloat(KeySnapLightIntensity, 0f);
            float snapLR   = zdo.GetFloat(KeySnapLightRange, 0f);

            foreach (var flicker in root.GetComponentsInChildren<LightFlicker>(includeInactive: true))
            {
                if (_fBaseIntensity != null && snapBase > 0f)
                    _fBaseIntensity.SetValue(flicker, snapBase);

                var l = _fLight != null ? (Light)_fLight.GetValue(flicker) : null;
                if (l != null)
                {
                    if (snapLI > 0f) l.intensity = snapLI;
                    if (snapLR > 0f) l.range = snapLR;
                }
            }

            ParticleSystem flare = null;
            foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(includeInactive: true))
            {
                string n = ps.gameObject.name.ToLowerInvariant();
                if (n.Contains("flare"))
                {
                    flare = ps;
                    break;
                }
            }

            if (flare != null)
            {
                float snapSize = zdo.GetFloat(KeySnapFlareSize, 0f);
                if (snapSize > 0f)
                {
                    var main = flare.main;
                    main.startSize = new ParticleSystem.MinMaxCurve(snapSize);
                }

                float sx = zdo.GetFloat(KeySnapFlareScaleX, 0f);
                float sy = zdo.GetFloat(KeySnapFlareScaleY, 0f);
                float sz = zdo.GetFloat(KeySnapFlareScaleZ, 0f);
                if (sx > 0f && sy > 0f && sz > 0f)
                    flare.transform.localScale = new Vector3(sx, sy, sz);

                var psr = flare.GetComponent<ParticleSystemRenderer>();
                if (psr != null)
                {
                    var mat = psr.material;
                    if (mat != null && mat.HasProperty("_TintColor"))
                    {
                        Color t = new Color(
                            zdo.GetFloat(KeySnapFlareTintR, 1f),
                            zdo.GetFloat(KeySnapFlareTintG, 1f),
                            zdo.GetFloat(KeySnapFlareTintB, 1f),
                            zdo.GetFloat(KeySnapFlareTintA, 0.5f)
                        );
                        mat.SetColor("_TintColor", t);
                    }
                }
            }
        }

        // Brute-force recovery path: re-scan all loaded torch-like objects and
        // reapply saved ZDO color/mult as if each torchcolor command was run again.
        public static void ReapplyAllLoadedFromZDO()
        {
            int scanned = 0;
            int applied = 0;

            foreach (var fp in Object.FindObjectsOfType<Fireplace>())
            {
                if (fp == null) continue;
                scanned++;

                var nview = fp.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) continue;

                string hex = nview.GetZDO().GetString(KeyHex, "");
                if (string.IsNullOrEmpty(hex)) continue;

                ApplyFromZDO(fp.transform, nview);
                applied++;
            }

            // Keep non-Fireplace Piece lights in the brute-force sweep as well.
            foreach (var nview in Object.FindObjectsOfType<ZNetView>())
            {
                if (nview == null || !nview.IsValid()) continue;
                if (nview.GetComponent<Piece>() == null) continue;
                if (nview.GetComponent<Fireplace>() != null) continue;

                scanned++;
                string hex = nview.GetZDO().GetString(KeyHex, "");
                if (string.IsNullOrEmpty(hex)) continue;

                ApplyFromZDO(nview.transform, nview);
                applied++;
            }

            if (applied > 0)
                Plugin.Log.LogInfo($"[TorchColor] periodic reapply sweep scanned={scanned} applied={applied}");
        }

        // ── LightFlicker post-init fix ─────────────────────────────────────────
        //
        // Called from LightFlickerAwakePatch after LightFlicker.Awake has finished.
        // At that point m_light and m_baseIntensity are freshly set from the prefab,
        // but m_light.color may have been reset to white (or left at the default).
        //
        // Problem: our Fireplace/ZNetView.Awake postfixes fire on the ROOT object
        // BEFORE child LightFlicker.Awake runs.  When we call Apply from those
        // postfixes m_light is null, so we color the Light directly via the
        // standalone loop.  LightFlicker.Awake then fires and may reset the color.
        //
        // Fix: after LightFlicker.Awake has run, re-apply the color to the now-
        // valid m_light.  Also correct m_baseIntensity, which LightFlicker.Awake
        // sets from the CURRENT (already-modified) light.intensity.
        public static void FixAfterLightFlickerAwake(LightFlicker flicker, ZNetView nview)
        {
            if (_fLight == null || _fBaseIntensity == null) return;
            if (nview == null || !nview.IsValid()) return;

            var zdo = nview.GetZDO();
            string hex = zdo.GetString(KeyHex, "");
            if (string.IsNullOrEmpty(hex)) return;
            if (!ColorUtility.TryParseHtmlString(hex, out Color color)) return;

            float mult = zdo.GetFloat(KeyMult, 1f);

            var light = (Light)_fLight.GetValue(flicker);
            if (light == null) return;

            int flickerId = flicker.GetInstanceID();

            // _origFlicker = prefab m_baseIntensity (1.0)
            // _origLight   = prefab light.intensity  (1.5) — a DIFFERENT field.
            // The old code synced them, making saved torches apply 1.5 * 0.35 = 0.525
            // instead of the correct 1.0 * 0.35 = 0.350, causing the oversized halo.
            float origBase = _origFlicker.TryGetValue(flickerId, out float cachedBase)
                ? cachedBase : 1f;

            float target = origBase * mult * 0.35f;
            _fBaseIntensity.SetValue(flicker, target);
            light.enabled   = true;
            light.color     = color;
            light.intensity = target;
        }
    }

    internal class TorchColorRuntimeEnforcer : MonoBehaviour
    {
        private ZNetView _nview;
        private float _nextEnforceTime;

        private void Awake()
        {
            _nview = GetComponent<ZNetView>();
        }

        private void OnEnable()
        {
            _nextEnforceTime = 0f;
        }

        private void LateUpdate()
        {
            if (Time.time < _nextEnforceTime) return;
            _nextEnforceTime = Time.time + 0.5f;

            if (_nview == null || !_nview.IsValid()) return;

            TorchColors.ApplyFromZDO(transform, _nview);
        }
    }

    // ── Patches ────────────────────────────────────────────────────────────────

    // Cache pristine intensities and apply any stored color right when the torch loads.
    [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.Awake))]
    public static class FireplaceAwakePatch
    {
        static void Postfix(Fireplace __instance)
        {
            TorchColors.EnsureRuntimeEnforcer(__instance.transform);
            TorchColors.CacheOriginals(__instance.transform);
            TorchColors.ApplyFromZDO(__instance);
            __instance.StartCoroutine(DeferredApply(__instance));
        }

        private static IEnumerator DeferredApply(Fireplace fp)
        {
            yield return null;
            if (fp == null) yield break;
            TorchColors.CacheOriginals(fp.transform);
            TorchColors.ApplyFromZDO(fp);
        }
    }

    // Re-apply after the fuel state changes (switches between high/low/off GameObjects).
    [HarmonyPatch(typeof(Fireplace), "UpdateState")]
    public static class FireplaceUpdateStatePatch
    {
        static void Postfix(Fireplace __instance)
        {
            TorchColors.CacheOriginals(__instance.transform);
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
            TorchColors.EnsureRuntimeEnforcer(__instance.transform);
            TorchColors.CacheOriginals(__instance.transform);
            TorchColors.ApplyFromZDO(__instance.transform, __instance);
            __instance.StartCoroutine(DeferredApply(__instance));
        }

        private static IEnumerator DeferredApply(ZNetView nview)
        {
            yield return null;
            if (nview == null || !nview.IsValid()) yield break;
            if (nview.GetComponent<Piece>() == null) yield break;
            if (nview.GetComponent<Fireplace>() != null) yield break;
            TorchColors.CacheOriginals(nview.transform);
            TorchColors.ApplyFromZDO(nview.transform, nview);
        }
    }

    // Re-apply color after a child LightFlicker has finished its own Awake.
    // This fires for every LightFlicker in the scene, so we guard with quick
    // component checks before doing any real work.
    //
    // Why this exists: Unity's Awake execution order means the root ZNetView /
    // Fireplace Awake postfixes run BEFORE child LightFlicker.Awake.  At that
    // point m_light is null, so color is applied via the standalone Light loop.
    // LightFlicker.Awake then runs and may reset m_light.color to white/default,
    // causing the "white round glow on reload" bug.
    [HarmonyPatch(typeof(LightFlicker), "Awake")]
    public static class LightFlickerAwakePatch
    {
        static void Postfix(LightFlicker __instance)
        {
            var nview = __instance.GetComponentInParent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;
            if (nview.GetComponent<Piece>() == null) return;
            TorchColors.FixAfterLightFlickerAwake(__instance, nview);
        }
    }

    // ── Console commands ───────────────────────────────────────────────────────
    [HarmonyPatch(typeof(Terminal), nameof(Terminal.Awake))]
    public static class TerminalPatch
    {
        private static bool _registered;

        private static bool IsLikelyLightSource(ZNetView nview)
        {
            if (nview == null) return false;
            if (nview.GetComponent<Fireplace>() != null) return true;
            if (nview.GetComponentInChildren<LightFlicker>(true) != null) return true;
            if (nview.GetComponentInChildren<Light>(true) != null) return true;
            return false;
        }

        private static string DescribeTarget(ZNetView nview)
        {
            if (nview == null) return "null";
            var root = nview.transform;
            var fp = nview.GetComponent<Fireplace>();
            var piece = nview.GetComponent<Piece>();
            return $"name='{root.name}' rootId={root.GetInstanceID()} nviewId={nview.GetInstanceID()} piece='{piece?.m_name ?? "(none)"}' fireplace={(fp != null)} pos={root.position}";
        }

        private static ZNetView ResolveLookTarget(Player player, out string source, out string context)
        {
            source = "none";
            context = "";

            var hoverObj = player.GetHoverObject();
            var hoverNview = hoverObj != null ? hoverObj.GetComponentInParent<ZNetView>() : null;
            bool hoverIsLight = IsLikelyLightSource(hoverNview);

            var cam = GameCamera.instance != null
                ? (Camera)typeof(GameCamera)
                    .GetField("m_camera", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .GetValue(GameCamera.instance)
                : Camera.main;

            string hitName = "nothing";
            float hitDist = -1f;
            ZNetView rayNview = null;
            if (cam != null)
            {
                var hits = Physics.RaycastAll(
                    cam.transform.position,
                    cam.transform.forward,
                    10f,
                    ~0,
                    QueryTriggerInteraction.Ignore);

                System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

                foreach (var hit in hits)
                {
                    if (hit.collider == null) continue;

                    // Keep the nearest physical hit for debug context.
                    if (hitName == "nothing")
                    {
                        hitName = hit.collider.gameObject.name;
                        hitDist = hit.distance;
                    }

                    var nview = hit.collider.GetComponentInParent<ZNetView>();
                    if (!IsLikelyLightSource(nview)) continue;

                    rayNview = nview;
                    hitName = hit.collider.gameObject.name;
                    hitDist = hit.distance;
                    break;
                }
            }

            bool rayIsLight = IsLikelyLightSource(rayNview);

            ZNetView selected = null;
            if (rayIsLight && hoverIsLight && rayNview != hoverNview)
            {
                var viewOrigin = cam != null ? cam.transform.position : player.transform.position;
                var viewForward = cam != null ? cam.transform.forward : player.transform.forward;

                float rayAngle = Vector3.Angle(viewForward, (rayNview.transform.position - viewOrigin).normalized);
                float hoverAngle = Vector3.Angle(viewForward, (hoverNview.transform.position - viewOrigin).normalized);

                if (hoverAngle <= rayAngle + 1f)
                {
                    selected = hoverNview;
                    source = "hover-preferred";
                }
                else
                {
                    selected = rayNview;
                    source = "raycast-preferred";
                }
            }
            else if (rayIsLight)
            {
                selected = rayNview;
                source = "raycast";
            }
            else if (hoverIsLight)
            {
                selected = hoverNview;
                source = "hover";
            }
            else if (rayNview != null)
            {
                selected = rayNview;
                source = "raycast-nonlight";
            }
            else if (hoverNview != null)
            {
                selected = hoverNview;
                source = "hover-nonlight";
            }

            context = $"hoverObj='{hoverObj?.name ?? "none"}' hoverNView={DescribeTarget(hoverNview)} raycast='{hitName}' rayDist={hitDist:F2} rayNView={DescribeTarget(rayNview)} chosen={DescribeTarget(selected)}";
            return selected;
        }

        private static IEnumerator TorchDiagWatch(ZNetView nview, float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            int tick = 0;
            while (nview != null && nview.IsValid() && Time.realtimeSinceStartup <= end)
            {
                TorchColors.Diagnose(nview, "watch#" + tick);
                tick++;
                yield return new WaitForSeconds(0.5f);
            }

            Plugin.Log.LogInfo($"[TorchDiag] watch done samples={tick} seconds={seconds:F1}");
        }

        private static string BuildPointLightConsoleSummary(ZNetView nview)
        {
            if (nview == null || !nview.IsValid())
                return "[TorchDiag] PointLight mode: target nview invalid";

            var zdo = nview.GetZDO();
            string hex = zdo != null ? zdo.GetString(TorchColors.KeyHex, "") : "";
            if (string.IsNullOrEmpty(hex)) hex = "(none)";
            float mult = zdo != null ? zdo.GetFloat(TorchColors.KeyMult, 1f) : 1f;

            Light point = null;
            foreach (var l in nview.transform.GetComponentsInChildren<Light>(includeInactive: true))
            {
                if (l == null || l.type != LightType.Point) continue;

                if (point == null) point = l;

                string n = l.gameObject.name.ToLowerInvariant();
                if (n.Contains("point"))
                {
                    point = l;
                    break;
                }
            }

            if (point == null)
                return $"[TorchDiag] PointLight mode: no Point Light found zdoHex={hex} mult={mult:F3}";

            var flicker = point.GetComponent<LightFlicker>();
            string flickerInfo = flicker != null
                ? $"flickerEnabled={flicker.enabled}"
                : "flicker=(none)";

            var lod = point.GetComponent<LightLod>();
            string lodInfo = lod != null
                ? $"lightLodCompEnabled={lod.enabled}"
                : "lightLod=(none)";

            return
                $"[TorchDiag] PointLight mode: name='{point.gameObject.name}' id={point.GetInstanceID()} enabled={point.enabled} active={point.gameObject.activeInHierarchy} type={point.type} renderMode={point.renderMode} shadows={point.shadows} intensity={point.intensity:F4} range={point.range:F2} color={point.color} {flickerInfo} {lodInfo} zdoHex={hex} mult={mult:F3}";
        }

        static void Postfix()
        {
            if (_registered) return;
            _registered = true;

            new Terminal.ConsoleCommand(
                "torchdiag",
                "Dump all glow-relevant component values for the torch you're looking at",
                args =>
                {
                    var player = Player.m_localPlayer;
                    if (player == null) { args.Context.AddString("No local player."); return; }

                    string source, context;
                    var nview = ResolveLookTarget(player, out source, out context);
                    if (nview == null || !IsLikelyLightSource(nview))
                    {
                        args.Context.AddString($"Not looking at a light source. {context}");
                        return;
                    }

                    TorchColors.Diagnose(nview, "manual");
                    args.Context.AddString(BuildPointLightConsoleSummary(nview));
                    args.Context.AddString($"[TorchDiag] Dumped via {source}: {DescribeTarget(nview)} — see BepInEx log for details.");
                },
                isCheat: true
            );

            new Terminal.ConsoleCommand(
                "torchdiagwatch",
                "torchdiagwatch [seconds] — sample diagnostics every 0.5s while redraw/LOD settles",
                args =>
                {
                    var player = Player.m_localPlayer;
                    if (player == null) { args.Context.AddString("No local player."); return; }

                    string source, context;
                    var nview = ResolveLookTarget(player, out source, out context);
                    if (nview == null || !IsLikelyLightSource(nview))
                    {
                        args.Context.AddString($"Not looking at a light source. {context}");
                        return;
                    }

                    float seconds = 8f;
                    if (args.Length > 1 && float.TryParse(args[1], out float parsed))
                        seconds = Mathf.Clamp(parsed, 1f, 30f);

                    player.StartCoroutine(TorchDiagWatch(nview, seconds));
                    args.Context.AddString($"[TorchDiag] Watching via {source}: {DescribeTarget(nview)} for {seconds:F1}s (0.5s interval). Check BepInEx log.");
                },
                isCheat: true
            );

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

                    string source, context;
                    var nview = ResolveLookTarget(player, out source, out context);
                    if (nview == null || !IsLikelyLightSource(nview))
                    {
                        args.Context.AddString($"Not looking at a light source. {context}");
                        args.Context.AddString("Aim at a torch, fire pit, or lantern and try again.");
                        return;
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
                    TorchColors.EnsureRuntimeEnforcer(root);

                    // ── Reset ──
                    if (args[1].ToLowerInvariant() == "reset")
                    {
                        zdo.Set(TorchColors.KeyHex, "");
                        zdo.Set(TorchColors.KeyMult, 1f);
                        TorchColors.ClearSnapshot(nview);
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
                    TorchColors.CaptureSnapshot(root, nview);

                    args.Context.AddString(
                        $"'{displayName}' → color={hexInput}  brightness×{mult:F2}");
                },
                isCheat: true
            );
        }
    }
}
