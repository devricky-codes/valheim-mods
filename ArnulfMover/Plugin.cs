using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ValheimArnulfMover
{
    // ── Entry point ────────────────────────────────────────────────────────────
    [BepInPlugin("com.yourname.arnulfmover", "ArnulfMover", "2.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        private readonly Harmony _harmony = new Harmony("com.yourname.arnulfmover");

        private void Awake()
        {
            Log = Logger;
            _harmony.PatchAll();
            Log.LogInfo("ArnulfMover 2.0 loaded. F8 = toggle AI navigation (ZInput + Ollama).");
        }
    }

    // ── ZInput.GetButton intercept ─────────────────────────────────────────────
    // Only overrides buttons we have explicitly set; all others fall through.
    [HarmonyPatch(typeof(ZInput), "GetButton")]
    public static class ZInputGetButtonPatch
    {
        static bool Prefix(string name, ref bool __result)
        {
            if (!Navigator.IsActive) return true;
            if (Navigator.InjectedButtons.TryGetValue(name, out bool v))
            {
                __result = v;
                return false;
            }
            return true;
        }
    }

    // ── ZInput.GetButtonDown intercept — one-shot for actions like "Use" ───────
    [HarmonyPatch(typeof(ZInput), "GetButtonDown")]
    public static class ZInputGetButtonDownPatch
    {
        static bool Prefix(string name, ref bool __result)
        {
            if (!Navigator.IsActive) return true;
            if (Navigator.InjectedButtonsDown.TryGetValue(name, out bool v) && v)
            {
                __result = true;
                Navigator.InjectedButtonsDown[name] = false; // consume once
                return false;
            }
            // Suppress normal GetButtonDown for movement buttons so we don't
            // get a false "just pressed" event from underlying key state.
            if (Navigator.InjectedButtons.ContainsKey(name))
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    // ── Attach Navigator to player on spawn ───────────────────────────────────
    [HarmonyPatch(typeof(Player), "Awake")]
    public static class PlayerAwakePatch
    {
        static void Postfix(Player __instance)
        {
            if (__instance.GetComponent<Navigator>() == null)
                __instance.gameObject.AddComponent<Navigator>();
        }
    }

    // ── Navigator ─────────────────────────────────────────────────────────────
    // Drives Arnulf using ZInput injection (WASD + Space + E) so movement goes
    // through the game's own input pipeline — no internal field hacks needed.
    // Ollama (llama3.1:8b) is queried every few seconds with world context to
    // decide where to walk next.
    internal class Navigator : MonoBehaviour
    {
        internal static bool IsActive;
        // Static dicts — read by ZInput patches on every ZInput call.
        internal static readonly Dictionary<string, bool> InjectedButtons     = new Dictionary<string, bool>();
        internal static readonly Dictionary<string, bool> InjectedButtonsDown = new Dictionary<string, bool>();

        private const float ArriveDist       = 1.8f;
        private const float RunThresholdDist = 12f;
        private const float NavInterval      = 4f;    // seconds between Ollama nav queries
        private const float IdleRestTimeout  = 6f;    // seconds to sit after "idle" decision

        private Vector3 _targetPos       = Vector3.zero;
        private bool    _hasTarget       = false;
        private bool    _pendingInteract = false;
        private float   _navTimer        = 0f;
        private float   _idleTimer       = 0f;
        private volatile bool _aiThinking = false;

        // Thread-safe pending target set from async AI response
        private readonly object  _pendingLock       = new object();
        private bool    _pendingTargetReady  = false;
        private Vector3 _pendingTarget       = Vector3.zero;
        private bool    _pendingIsInteract   = false;
        private bool    _pendingIsIdle       = false;
        private string  _pendingActivity     = "";
        private string  _lastActivity        = "wandering thoughtfully";
        private float   _philosophyTimer     = 0f;
        private string  _pendingPhilosophy   = "";
        private bool    _pendingPhilosophyReady = false;

        private static readonly HttpClient _http =
            new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        // ── F8 toggle + main loop ─────────────────────────────────────────────
        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F8))
            {
                IsActive = !IsActive;
                if (!IsActive)
                {
                    ClearAllButtons();
                    _hasTarget  = false;
                    _idleTimer  = 0f;
                    Plugin.Log.LogInfo("ArnulfMover OFF — returning control to player.");
                    MessageHud.instance?.ShowMessage(
                        MessageHud.MessageType.Center, "Arnulf: I'll follow your lead.");
                }
                else
                {
                    _navTimer  = 0f;  // ask AI immediately on enable
                    _idleTimer = 0f;
                    Plugin.Log.LogInfo("ArnulfMover ON — AI navigation (ZInput) active.");
                    MessageHud.instance?.ShowMessage(
                        MessageHud.MessageType.Center, "[F8] Arnulf roams by his own counsel.");
                    LogZInputButtonNames();
                }
            }

            if (!IsActive) return;

            var player = Player.m_localPlayer;
            if (player == null) return;

            // Apply pending nav target and philosophy from async AI thread
            lock (_pendingLock)
            {
                if (!string.IsNullOrEmpty(_pendingActivity))
                {
                    _lastActivity    = _pendingActivity;
                    _pendingActivity = "";
                }
                if (_pendingTargetReady)
                {
                    _pendingTargetReady = false;
                    if (_pendingIsIdle)
                    {
                        _hasTarget = false;
                        _idleTimer = IdleRestTimeout;
                        ClearMovementButtons();
                        if (_philosophyTimer <= 0f)
                        {
                            _philosophyTimer = 22f;
                            _ = AskPhilosophyAsync();
                        }
                    }
                    else
                    {
                        _targetPos       = _pendingTarget;
                        _hasTarget       = true;
                        _pendingInteract = _pendingIsInteract;
                        _idleTimer       = 0f;
                    }
                }
                if (_pendingPhilosophyReady)
                {
                    _pendingPhilosophyReady = false;
                    ShowPhilosophy(_pendingPhilosophy);
                }
            }

            // Idle countdown — sit still for a few seconds when AI chose "idle"
            if (_idleTimer > 0f)
            {
                _idleTimer -= Time.deltaTime;
                ClearMovementButtons();
            }
            else if (_hasTarget)
            {
                SteerToTarget(player);
            }
            else
            {
                ClearMovementButtons();
            }

            // Timers
            if (_philosophyTimer > 0f) _philosophyTimer -= Time.deltaTime;

            // Periodic Ollama nav query
            _navTimer -= Time.deltaTime;
            if (_navTimer <= 0f && !_aiThinking)
            {
                _navTimer = NavInterval;
                _ = AskNavAsync(player);
            }
        }

        // ── Steering ─────────────────────────────────────────────────────────
        // Computes which ZInput buttons to inject based on camera-relative direction
        // to target, so movement goes in the correct world direction regardless of
        // where the camera is pointing.
        private void SteerToTarget(Player player)
        {
            var pos      = player.transform.position;
            var toTarget = new Vector3(_targetPos.x - pos.x, 0f, _targetPos.z - pos.z);
            float dist   = toTarget.magnitude;

            if (dist < ArriveDist)
            {
                _hasTarget = false;
                ClearMovementButtons();
                if (_pendingInteract)
                {
                    InjectedButtonsDown["Use"] = true;
                    _pendingInteract = false;
                    Plugin.Log.LogInfo("Nav: arrived — firing Use");
                    if (_philosophyTimer <= 0f)
                    {
                        _philosophyTimer = 28f;
                        _ = AskPhilosophyAsync();
                    }
                }
                return;
            }

            // Rotate character model to face target (cosmetic + helps with slopes)
            player.transform.rotation = Quaternion.Slerp(
                player.transform.rotation,
                Quaternion.LookRotation(toTarget.normalized),
                Time.deltaTime * 6f);

            // Get camera horizontal forward/right so we can decompose target direction
            // into the F/B/L/R buttons that move us toward it.
            var camFwd = Vector3.forward;
            if (GameCamera.instance != null)
            {
                camFwd   = GameCamera.instance.transform.forward;
                camFwd.y = 0f;
                if (camFwd.sqrMagnitude > 0.001f) camFwd.Normalize();
                else camFwd = player.transform.forward;
            }
            // Camera right = 90° CW rotation of camera forward in XZ plane
            var camRight = new Vector3(camFwd.z, 0f, -camFwd.x);

            var dir      = toTarget.normalized;
            float fDot   = Vector3.Dot(dir, camFwd);
            float rDot   = Vector3.Dot(dir, camRight);
            bool run     = dist > RunThresholdDist;

            InjectedButtons["Forward"]  = fDot  >  0.25f;
            InjectedButtons["Backward"] = fDot  < -0.25f;
            InjectedButtons["Right"]    = rDot  >  0.25f;
            InjectedButtons["Left"]     = rDot  < -0.25f;
            InjectedButtons["Run"]      = run;
        }

        private static void ClearMovementButtons()
        {
            InjectedButtons["Forward"]  = false;
            InjectedButtons["Backward"] = false;
            InjectedButtons["Left"]     = false;
            InjectedButtons["Right"]    = false;
            InjectedButtons["Run"]      = false;
        }

        private static void ClearAllButtons()
        {
            InjectedButtons.Clear();
            InjectedButtonsDown.Clear();
        }

        // ── Ollama navigation decision ─────────────────────────────────────────
        private async Task AskNavAsync(Player player)
        {
            _aiThinking = true;
            try
            {
                var pos        = player.transform.position;
                var worldCtx   = BuildWorldContext(pos);

                var userMsg =
                    $"Your position: ({pos.x:F1}, {pos.z:F1})\n\n" +
                    $"{worldCtx}\n" +
                    "Decide what Arnulf does next. Pick ONE comfort object to go to and USE (press E) if possible. " +
                    "Prefer objects you are NOT currently at. " +
                    "If nothing interesting is nearby, idle briefly.\n" +
                    "Reply with ONLY one JSON object, nothing else:\n" +
                    "  Go and interact (sit/soak/warm/rest): {\"action\":\"interact\",\"x\":12.3,\"z\":45.6,\"why\":\"sitting by fire\"}\n" +
                    "  Just walk somewhere: {\"action\":\"walk_to\",\"x\":12.3,\"z\":45.6,\"why\":\"exploring\"}\n" +
                    "  Stay and contemplate: {\"action\":\"idle\",\"why\":\"resting\"}";

                var systemContent =
                    "You are Arnulf, a wry Viking philosopher whose sole ambition right now is relaxation. " +
                    "You are given your world position and a categorised list of nearby comfort objects with their exact coordinates.\n" +
                    "\nYour priority order for relaxation:\n" +
                    "1. SEATING — chairs, benches, thrones, hammocks. Walk to one and use it (action: interact).\n" +
                    "2. BATHING — hot tubs, stone baths, saunas. Walk right up and use it (action: interact).\n" +
                    "3. WARMTH — fireplaces, fire pits, hearths. Stand very close and warm yourself (action: interact if usable, else walk_to).\n" +
                    "4. SLEEPING — beds. Use if tired (action: interact).\n" +
                    "5. EXPLORATION — if none of the above exist, stroll to any interesting structure.\n" +
                    "6. IDLE — if you are already at a comfort object and satisfied, stay put briefly.\n" +
                    "\nRules:\n" +
                    "- ALWAYS pick the closest comfort object of the highest-priority category available.\n" +
                    "- Use the EXACT x,z coordinates listed in the context for your chosen object.\n" +
                    "- Never invent coordinates not in the list.\n" +
                    "- Reply with ONLY the JSON object. No prose, no explanation.";

                var payload = new
                {
                    model    = "llama3.1:8b",
                    messages = new[]
                    {
                        new { role = "system", content = systemContent },
                        new { role = "user",   content = userMsg }
                    },
                    stream = false
                };

                var resp = await _http.PostAsync(
                    "http://localhost:11434/api/chat",
                    new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json"));

                var raw     = await resp.Content.ReadAsStringAsync();
                var content = JObject.Parse(raw)?["message"]?["content"]?.ToString();

                if (!string.IsNullOrEmpty(content))
                {
                    Plugin.Log.LogInfo($"Nav AI: {content.Trim()}");
                    ApplyNavResponse(content, pos);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Nav AI error: {e.Message}");
            }
            _aiThinking = false;
        }

        private void ApplyNavResponse(string response, Vector3 currentPos)
        {
            try
            {
                int start = response.IndexOf('{');
                int end   = response.LastIndexOf('}');
                if (start < 0 || end <= start) return;

                var obj    = JObject.Parse(response.Substring(start, end - start + 1));
                var action = obj["action"]?.ToString() ?? "idle";

                lock (_pendingLock)
                {
                    var why = obj["why"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(why)) _pendingActivity = why;

                    if (action == "walk_to" || action == "interact")
                    {
                        float x = obj["x"]?.Value<float>() ?? currentPos.x;
                        float z = obj["z"]?.Value<float>() ?? currentPos.z;
                        _pendingTarget      = new Vector3(x, currentPos.y, z);
                        _pendingIsInteract  = action == "interact";
                        _pendingIsIdle      = false;
                        _pendingTargetReady = true;
                        Plugin.Log.LogInfo($"Nav queued: {action} → ({x:F1}, {z:F1})  [{why}]");
                    }
                    else
                    {
                        _pendingIsIdle      = true;
                        _pendingIsInteract  = false;
                        _pendingTargetReady = true;
                        Plugin.Log.LogInfo($"Nav queued: idle  [{why}]");
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Nav parse error: {e.Message}");
            }
        }

        // ── Philosophy on idle / arrival ──────────────────────────────────────
        private async Task AskPhilosophyAsync()
        {
            try
            {
                var activity = _lastActivity;
                var payload = new
                {
                    model    = "llama3.1:8b",
                    messages = new[]
                    {
                        new
                        {
                            role    = "system",
                            content =
                                "You are Arnulf, a dry, wry Viking philosopher. " +
                                "Speak entirely in first person, one or two sentences maximum. " +
                                "Contemplate your current moment — warmth, mortality, comfort, the absurdity of " +
                                "existence, the impermanence of things, the weight of being alive right now. " +
                                "Deadpan wit is encouraged. Never announce that you are philosophizing. " +
                                "No stage directions. No quotation marks. Just the thought, as if to yourself."
                        },
                        new
                        {
                            role    = "user",
                            content = $"You are currently: {activity}. Share one thought."
                        }
                    },
                    stream = false
                };

                var resp = await _http.PostAsync(
                    "http://localhost:11434/api/chat",
                    new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json"));

                var raw  = await resp.Content.ReadAsStringAsync();
                var text = JObject.Parse(raw)?["message"]?["content"]?.ToString()?.Trim();

                if (!string.IsNullOrEmpty(text))
                {
                    Plugin.Log.LogInfo($"[Arnulf muses] {text}");
                    lock (_pendingLock)
                    {
                        _pendingPhilosophy      = text;
                        _pendingPhilosophyReady = true;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Philosophy AI error: {e.Message}");
            }
        }

        private static void ShowPhilosophy(string text)
        {
            if (Chat.instance == null || string.IsNullOrEmpty(text)) return;
            try
            {
                // Floating speech bubble via private Chat.AddInworldText —
                // same reflection trick as the Philosopher plugin.
                var player = Player.m_localPlayer;
                if (player != null)
                {
                    var mAdd = AccessTools.Method(typeof(Chat), "AddInworldText");
                    if (mAdd != null)
                    {
                        var pms = mAdd.GetParameters();
                        if (pms.Length >= 5)
                        {
                            // Build a minimal UserInfo
                            var userInfoType = typeof(Chat).Assembly.GetType("UserInfo");
                            object userInfo  = null;
                            if (userInfoType != null)
                            {
                                userInfo = System.Activator.CreateInstance(userInfoType);
                                var fName = userInfoType.GetField("Name",
                                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                                fName?.SetValue(userInfo, "Arnulf");
                            }
                            if (userInfo != null)
                            {
                                // Signature: (Talker.Type, long, Vector3, UserInfo, string)
                                mAdd.Invoke(Chat.instance, new object[]
                                {
                                    Talker.Type.Normal,
                                    0L,
                                    player.transform.position,
                                    userInfo,
                                    text
                                });
                                return;
                            }
                        }
                    }
                }
            }
            catch { /* fall through to chat log */ }
            // Fallback: chat log entry with warm amber tint
            Chat.instance.AddString($"<color=#c8a87a>Arnulf: {text}</color>");
        }

        // Comfort object catalogue — maps prefab name fragments to (human label, category, canInteract)
        // Category: 1=seating 2=bathing 3=warmth 4=sleeping 5=other
        private static readonly (string fragment, string label, int category, bool interact)[] s_comfortMap =
        {
            // Seating
            ("piece_chair",        "chair",          1, true),
            ("piece_bench",        "bench",          1, true),
            ("piece_throne",       "throne",         1, true),
            ("piece_hammock",      "hammock",        1, true),
            ("piece_stool",        "stool",          1, true),
            ("piece_sofa",         "sofa",           1, true),
            // Bathing
            ("piece_bathtub",      "stone bath",     2, true),
            ("piece_jacuzzi",      "hot tub",        2, true),
            ("piece_hot",          "hot tub",        2, true),
            ("sauna",              "sauna",          2, true),
            // Warmth
            ("hearth",             "hearth",         3, false),
            ("fire_pit",           "fire pit",       3, false),
            ("fireplace",          "fireplace",      3, false),
            ("bonfire",            "bonfire",        3, false),
            ("piece_groundtorch",  "ground torch",   3, false),
            ("piece_walltorch",    "wall torch",     3, false),
            ("Fireplace",          "fireplace",      3, false),
            // Sleeping
            ("piece_bed",          "bed",            4, true),
            ("dragonbed",          "dragon bed",     4, true),
            // Tables (lower priority, but interesting for resting near)
            ("piece_table",        "table",          5, false),
            ("piece_blackmarble",  "marble structure",5,false),
        };

        // ── World context builder ──────────────────────────────────────────────
        private static string BuildWorldContext(Vector3 pos)
        {
            var cols = Physics.OverlapSphere(pos, 30f,
                LayerMask.GetMask("piece", "piece_nonsolid", "item"));

            // Deduplicate by object root
            var seen     = new HashSet<int>();
            var comfort  = new List<string>();   // priority objects
            var others   = new List<string>();   // everything else

            foreach (var col in cols)
            {
                var znet = col.GetComponentInParent<ZNetView>();
                if (znet == null) continue;
                int id = znet.gameObject.GetInstanceID();
                if (!seen.Add(id)) continue;

                var p        = col.transform.position;
                var rawName  = znet.gameObject.name.Replace("(Clone)", "").Trim().ToLower();

                // Match against comfort catalogue
                bool matched = false;
                foreach (var entry in s_comfortMap)
                {
                    if (rawName.Contains(entry.fragment.ToLower()))
                    {
                        var tag    = entry.interact ? " [use E]" : " [warm spot]";
                        var dist   = Mathf.RoundToInt(Vector3.Distance(pos, p));
                        comfort.Add(
                            $"  [{CategoryName(entry.category)}] {entry.label}{tag}" +
                            $" at ({p.x:F1}, {p.z:F1}) — {dist}m away");
                        matched = true;
                        break;
                    }
                }
                if (!matched && others.Count < 6)
                {
                    others.Add($"  [structure] {rawName} at ({p.x:F1}, {p.z:F1})");
                }
            }

            var sb = new StringBuilder();
            if (comfort.Count > 0)
            {
                // Sort comfort items by category priority then distance
                comfort.Sort((a, b) => string.Compare(a, b, StringComparison.Ordinal));
                sb.AppendLine("COMFORT OBJECTS (use these):");
                foreach (var l in comfort) sb.AppendLine(l);
            }
            else
            {
                sb.AppendLine("COMFORT OBJECTS: none found within 30m");
            }

            if (others.Count > 0)
            {
                sb.AppendLine("OTHER STRUCTURES:");
                foreach (var l in others) sb.AppendLine(l);
            }

            return sb.ToString();
        }

        private static string CategoryName(int cat)
        {
            switch (cat)
            {
                case 1: return "SEATING";
                case 2: return "BATHING";
                case 3: return "WARMTH";
                case 4: return "SLEEPING";
                default: return "OTHER";
            }
        }

        // ── Diagnostic: log all ZInput button names on first F8 ───────────────
        private static void LogZInputButtonNames()
        {
            try
            {
                var fButtons = AccessTools.Field(typeof(ZInput), "m_buttons");
                if (fButtons == null || ZInput.instance == null)
                {
                    Plugin.Log.LogWarning("Nav diagnostic: ZInput.m_buttons field not found.");
                    return;
                }
                var dict = fButtons.GetValue(ZInput.instance);
                if (dict == null) return;
                var keysProp = dict.GetType().GetProperty("Keys");
                var keys     = keysProp?.GetValue(dict) as System.Collections.IEnumerable;
                if (keys == null) return;
                var names = new List<string>();
                foreach (var k in keys) names.Add(k.ToString());
                Plugin.Log.LogInfo("ZInput buttons available: " + string.Join(", ", names));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"LogZInputButtonNames error: {e.Message}");
            }
        }
    }
}
