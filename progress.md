# CustomValheim — Progress Log

## What We've Built

### Mod 1 — TerrainLeveler ✅
- Console command `levelterrain <radius> [offset]` — flattens terrain in a radius around the player
- Negative offset doubles as a dig command (raises walls, carves pits)
- `undoterrain` — full undo stack restoring all modified height/paint/mask data
- Patched `Terminal.Awake` (private) via string literal; called private `DoOperation` + `Save` via reflection
- Deployed: `ValheimTerrainLeveler.dll`

### Mod 2 — TorchColor ✅
- Console command `torchcolor <#RRGGBB|reset> [brightness]`
- Tints both the `Light` component and the flame `ParticleSystem` so the whole torch changes colour
- Persists across world saves via ZDO (`tc_hex`, `tc_mult` keys) — colours survive logout
- Re-applied on `Fireplace.Awake` and private `UpdateState` so respawned torches keep their colour
- Deployed: `ValheimTorchColor.dll`

### Mod 3 — Philosopher (Arnulf) ✅
- AI philosopher companion powered by local Ollama (`llama3.1:8b`)
- **Idle musings** — Arnulf observes the world unprompted on a 30 s idle timer
- **Interact hook** — press E on any object and Arnulf reacts to what was interacted with
- **Build hook** — place a piece and Arnulf comments on the construction
- **Chat hook** — type anything in chat and Arnulf replies directly to you
- **World context** — every prompt includes: time of day, weather, nearby built pieces (top 8 by count)
- **Rolling history** — 28 k-char sliding window keeps conversation coherent
- **Floating speech bubble** — response appears above the player's head via `Chat.AddInworldText`
  (with chat-box fallback), labelled "Arnulf"
- **Large-response splitting** — responses longer than ~120 chars are broken at sentence boundaries
  and displayed 15 s apart so reading never feels rushed
- Deployed: `ValheimPhilosopher.dll` + `Newtonsoft.Json.dll`

---

## Key Lessons / Patterns Discovered

| Problem | Solution |
|---|---|
| Private game methods in patches | Use string literal e.g. `"Awake"` instead of `nameof()` |
| Private methods called at runtime | `typeof(X).GetMethod("...", BindingFlags.NonPublic\|Instance)` |
| HttpClient missing on net472 | Add `<Reference Include="System.Net.Http" />` to csproj |
| Private static fields (EnvMan, Chat) | Use public wrapper methods/properties: `EnvMan.IsDay()`, `Chat.instance` |
| Splatform dependency pulled in by AddString overload | Add `<Reference Include="Splatform" />` to csproj |
| async on ThreadPool for Unity | Use `Task.Run(async () => ...)` not `ThreadPool.QueueUserWorkItem(async _=> ...)` |
| Chat messages appear in chatbox only | Call private `Chat.AddInworldText` via reflection → floating world bubble |

---

## Stack
- **BepInEx 5** — plugin host via `winhttp.dll` doorstop
- **Harmony 2** — JIT patching of game methods
- **Ollama** — local LLM server, model `llama3.1:8b`
- **Newtonsoft.Json 13.0.3** — JSON serialisation for Ollama REST API
- **Target**: `net472` (C# 7.3) — no static local functions, no nullable reference types
- **Valheim install**: `F:\SteamLibrary\steamapps\common\Valheim`
- **Project root**: `F:\Projects\CustomValheim\`

---

## Ideas / Next Steps
- [ ] Give Arnulf a distinct sound cue (short ambient clip) when he speaks
- [ ] Let Arnulf reference the player's name (read from `Player.GetPlayerName()`)
- [ ] Arnulf reacts to death / respawn events
- [ ] Arnulf reacts to sunrise / sunset transitions
- [ ] Configurable Ollama model via `config.cfg`
- [ ] Persist Arnulf's mood/state across sessions via ZDO or a local JSON file
