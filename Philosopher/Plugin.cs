using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace ValheimPhilosopher
{
    // ── Entry point ────────────────────────────────────────────────────────────
    [BepInPlugin("com.yourname.philosopher", "Philosopher", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        private readonly Harmony _harmony = new Harmony("com.yourname.philosopher");

        private void Awake()
        {
            Log = Logger;
            _harmony.PatchAll();
            Log.LogInfo("Philosopher loaded — Arnulf contemplates.");
        }
    }

    // ── Ollama client ──────────────────────────────────────────────────────────
    internal static class OllamaClient
    {
        private const string Url   = "http://localhost:11434/api/chat";
        private const string Model = "llama3.1:8b";

        // System prompt — Arnulf's complete personality.
        // This is kept in every request so the 8k window always contains it.
        private const string System =
            "You are Arnulf, an ancient Norse philosopher who has seen enough of the world — " +
            "battles, kings, winters, gods, and frankly, far too many dramatic monologues. " +
            "Now you want only quiet. You live inside Valheim, a purgatory of mist and stone, " +
            "and you have made a grudging peace with it. " +
            "You speak in short, unhurried observations — one or two sentences at most. " +
            "You find meaning in small things: the angle of smoke, the grain of wood, " +
            "the weight of a stone in the hand. You are not grim — you are wry. " +
            "You carry the dry humour of a man who has outlived every god he ever feared. " +
            "A good quip lands better than a sermon, and you know it. " +
            "Your wit is deadpan — you never announce a joke, you just say the thing. " +
            "You think often about the deeper currents beneath ordinary moments — " +
            "mortality, the passage of time, why men build things that will outlast them, " +
            "the strange comfort of repetition, what it means to rest, what it means to be forgotten. " +
            "The surroundings are a doorway, not a destination: a fire is also impermanence, " +
            "a stone wall is also the desire to be remembered, rain is also time passing. " +
            "Let the context spark a thought, but let the thought go wherever it needs to go — " +
            "sometimes that is the immediate world, sometimes it is the nature of existence itself. " +
            "You occasionally ask the wanderer a question, but not every time. " +
            "You never repeat yourself word for word. Never say 'Indeed' or 'Ah'. " +
            "Never start with 'I'. Speak as if to yourself, just loud enough to be overheard. " +
            "If the situation is absurd, acknowledge it. If the wanderer does something impressive, " +
            "a small compliment is fine. If they do something ridiculous, say so — gently.";

        // Rolling conversation history — trimmed to stay inside 8k tokens.
        // Rough estimate: 1 token ≈ 4 chars. 8192 tokens ≈ 32768 chars.
        // We keep 28000 chars for history to leave room for system + context.
        private static readonly List<OllamaMessage> _history = new List<OllamaMessage>();
        private const int MaxHistoryChars = 28000;
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        public static async Task<string> AskAsync(string userContent)
        {
            // Add user turn
            _history.Add(new OllamaMessage { role = "user", content = userContent });
            TrimHistory();

            var body = new OllamaRequest
            {
                model    = Model,
                stream   = false,
                messages = BuildMessages()
            };

            var json    = JsonConvert.SerializeObject(body);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                var response   = await _http.PostAsync(Url, content);
                var raw        = await response.Content.ReadAsStringAsync();
                var result     = JsonConvert.DeserializeObject<OllamaResponse>(raw);
                var reply      = result?.message?.content?.Trim() ?? "(silence)";

                // Add assistant turn to history
                _history.Add(new OllamaMessage { role = "assistant", content = reply });
                TrimHistory();

                return reply;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Ollama unreachable: {ex.Message}");
                return null;
            }
        }

        private static List<OllamaMessage> BuildMessages()
        {
            var messages = new List<OllamaMessage>
            {
                new OllamaMessage { role = "system", content = System }
            };
            messages.AddRange(_history);
            return messages;
        }

        private static void TrimHistory()
        {
            int total = _history.Sum(m => m.content.Length);
            while (total > MaxHistoryChars && _history.Count > 2)
            {
                total -= _history[0].content.Length;
                _history.RemoveAt(0);
            }
        }

        // JSON shapes for Ollama /api/chat
        private class OllamaRequest
        {
            public string              model    { get; set; }
            public bool                stream   { get; set; }
            public List<OllamaMessage> messages { get; set; }
        }

        private class OllamaResponse
        {
            public OllamaMessage message { get; set; }
        }
    }

    internal class OllamaMessage
    {
        public string role    { get; set; }
        public string content { get; set; }
    }

    // ── Context builder ────────────────────────────────────────────────────────
    // Gathers everything happening around the player right now.
    internal static class WorldContext
    {
        private const float ScanRadius = 30f;

        public static string Build(string eventHint = null)
        {
            var sb = new StringBuilder();

            var player = Player.m_localPlayer;
            if (player == null) return eventHint ?? "Nothing.";

            // Time & weather
            if (EnvMan.instance != null)
            {
                var env    = EnvMan.instance.GetCurrentEnvironment();
                string tod = EnvMan.IsDay()
                    ? (EnvMan.instance.GetDayFraction() > 0.5f ? "afternoon" : "morning")
                    : "night";

                sb.Append($"It is {tod}");
                if (env != null) sb.Append($", {env.m_name}");
                if (EnvMan.IsWet())       sb.Append(", raining");
                if (EnvMan.IsCold())      sb.Append(", cold");
                if (EnvMan.IsFreezing())  sb.Append(", freezing");
                sb.AppendLine(".");
            }

            // Nearby built pieces within radius
            var pos     = player.transform.position;
            var pieces  = new List<string>();
            var cols    = Physics.OverlapSphere(pos, ScanRadius,
                              LayerMask.GetMask("piece", "piece_nonsolid"));

            var seen = new Dictionary<string, int>();
            foreach (var col in cols)
            {
                var piece = col.GetComponentInParent<Piece>();
                if (piece == null || string.IsNullOrEmpty(piece.m_name)) continue;
                string name = piece.m_name;
                if (!seen.ContainsKey(name)) seen[name] = 0;
                seen[name]++;
            }

            if (seen.Count > 0)
            {
                // Top 8 most numerous — avoid flooding the context
                var top = seen.OrderByDescending(kv => kv.Value).Take(8);
                sb.Append("Nearby: ");
                sb.AppendLine(string.Join(", ", top.Select(kv =>
                    kv.Value > 1 ? $"{kv.Key} ×{kv.Value}" : kv.Key)));
            }

            // What the player's cursor is pointing at right now
            var hoverObj = player.GetHoverObject();
            if (hoverObj != null)
            {
                string raw       = hoverObj.name.Replace("(Clone)", "").Trim();
                string hoverName = raw.Replace("piece_", "").Replace("_", " ").Trim();
                if (!string.IsNullOrEmpty(hoverName))
                    sb.AppendLine($"The wanderer is looking at: {hoverName}.");
            }

            // Event hint (what triggered this)
            if (!string.IsNullOrEmpty(eventHint))
                sb.AppendLine(eventHint);

            return sb.ToString().Trim();
        }
    }

    // ── Response queue — bridges async → Unity main thread ────────────────────
    internal static class ResponseQueue
    {
        private static readonly System.Collections.Concurrent.ConcurrentQueue<string> _queue
            = new System.Collections.Concurrent.ConcurrentQueue<string>();

        public static void Enqueue(string text) => _queue.Enqueue(text);

        // Called from a MonoBehaviour Update — drains one message per frame
        public static bool TryDequeue(out string text) => _queue.TryDequeue(out text);
    }

    // ── MonoBehaviour tick — owns the idle timer and response drain ────────────
    internal class PhilosopherTick : MonoBehaviour
    {
        private float  _idleTimer        = 30f;  // first thought at 30s, then every 30s
        private float  _partTimer        = 0f;   // countdown between split-response parts
        private bool   _busy             = false;
        private int    _pendingParts     = 0;    // parts still in queue or waiting to display

        private void Update()
        {
            // Drain split-response parts — one every 15 s
            if (_partTimer > 0f)
            {
                _partTimer -= Time.deltaTime;
            }
            else if (ResponseQueue.TryDequeue(out string line))
            {
                ShowInChat(line);
                _partTimer = 7f;
                // If that was the last part, unlock for the next trigger
                if (System.Threading.Interlocked.Decrement(ref _pendingParts) <= 0)
                    _busy = false;
            }

            var player = Player.m_localPlayer;
            if (player == null) return;

            // Suppress during active combat
            if (player.InAttack() || player.IsBlocking()) return;

            _idleTimer -= Time.deltaTime;
            if (_idleTimer <= 0f && !_busy)
            {
                _idleTimer = 30f;
                // Pass null so the prompt is purely the built world context —
                // time, weather, nearby pieces — with no artificial static hint.
                TriggerAsync(null);
            }
        }

        internal void TriggerAsync(string eventHint, bool isPlayerChat = false, string playerText = null)
        {
            if (_busy) return;
            _busy = true;

            string context = isPlayerChat
                ? $"{WorldContext.Build()}\nThe wanderer says to Arnulf: \"{playerText}\""
                : WorldContext.Build(eventHint);

            Task.Run(async () =>
            {
                string reply = await OllamaClient.AskAsync(context);
                if (!string.IsNullOrEmpty(reply))
                {
                    // Split long replies at sentence boundaries so each part
                    // appears as a separate bubble 7 s apart.
                    var parts = SplitIntoParts(reply);
                    foreach (string part in parts)
                    {
                        System.Threading.Interlocked.Increment(ref _pendingParts);
                        ResponseQueue.Enqueue(part);
                    }
                    // _busy stays true — cleared in Update() after last part is shown
                }
                else
                {
                    _busy = false; // nothing to show, unblock immediately
                }
            });
        }

        // Splits a reply into <=120-char sentence chunks so long responses
        // are displayed gradually rather than all at once.
        private static List<string> SplitIntoParts(string reply)
        {
            var parts     = new List<string>();
            // Split on sentence-ending punctuation followed by a space or end
            var sentences = System.Text.RegularExpressions.Regex
                .Split(reply.Trim(), @"(?<=[.!?])\s+");

            var current = new StringBuilder();
            foreach (string s in sentences)
            {
                if (s.Length == 0) continue;
                // If adding this sentence keeps us under 120 chars, bundle it
                if (current.Length > 0 && current.Length + 1 + s.Length <= 120)
                {
                    current.Append(' ');
                    current.Append(s);
                }
                else
                {
                    if (current.Length > 0)
                        parts.Add(current.ToString());
                    current.Clear();
                    current.Append(s);
                }
            }
            if (current.Length > 0)
                parts.Add(current.ToString());

            return parts.Count > 0 ? parts : new List<string> { reply };
        }

        internal static void ShowInChat(string text)
        {
            var player = Player.m_localPlayer;
            if (player == null || Chat.instance == null) return;

            // Call the private AddInworldText so the speech appears as a floating
            // bubble above the player's head, labelled "Arnulf".
            var method = typeof(Chat).GetMethod(
                "AddInworldText",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            if (method != null)
            {
                // UserInfo is a struct — create boxed copy and set the name field.
                object userInfo = Activator.CreateInstance(typeof(UserInfo));
                typeof(UserInfo).GetField("Name").SetValue(userInfo, "Arnulf");

                method.Invoke(Chat.instance, new object[]
                {
                    player.gameObject,
                    player.GetPlayerID(),
                    player.transform.position,
                    Talker.Type.Normal,
                    userInfo,
                    text
                });
            }
            else
            {
                // Fallback: write to chat box if reflection fails.
                Chat.instance.AddString("Arnulf", text, Talker.Type.Whisper, false);
            }
        }
    }

    // ── Patches ────────────────────────────────────────────────────────────────

    // Spawn the tick component when a Player loads into the world.
    // Player.Awake is private — use string literal.
    [HarmonyPatch(typeof(Player), "Awake")]
    public static class PlayerAwakePatch
    {
        static void Postfix(Player __instance)
        {
            if (__instance.GetComponent<PhilosopherTick>() == null)
                __instance.gameObject.AddComponent<PhilosopherTick>();
        }
    }

    // Hook: player presses E on anything
    [HarmonyPatch(typeof(Player), "Interact")]   // private — use string literal
    public static class InteractPatch
    {
        static void Postfix(Player __instance, GameObject go)
        {
            if (__instance != Player.m_localPlayer || go == null) return;
            var tick = __instance.GetComponent<PhilosopherTick>();
            if (tick == null) return;

            // Give it a human-readable hint (strip "piece_" prefix, replace _ with space)
            string raw  = go.name.Replace("(Clone)", "").Trim();
            string name = raw.Replace("piece_", "").Replace("_", " ").Trim();
            tick.TriggerAsync($"The wanderer pressed E on: {name}.");
        }
    }

    // Hook: player places a built piece
    [HarmonyPatch(typeof(Player), nameof(Player.PlacePiece))]
    public static class PlacePiecePatch
    {
        static void Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            var tick = __instance.GetComponent<PhilosopherTick>();
            tick?.TriggerAsync("The wanderer just finished placing a building piece.");
        }
    }

    // Hook: player sends a chat message — becomes a direct question to Arnulf
    [HarmonyPatch(typeof(Chat), nameof(Chat.SendText))]
    public static class ChatSendPatch
    {
        static void Postfix(Talker.Type type, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            // Ignore slash-commands and whispers to other players
            if (text.StartsWith("/")) return;

            var player = Player.m_localPlayer;
            if (player == null) return;

            var tick = player.GetComponent<PhilosopherTick>();
            tick?.TriggerAsync(null, isPlayerChat: true, playerText: text);
        }
    }
}
