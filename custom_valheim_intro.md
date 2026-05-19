# Custom Valheim Modding — Intro & Learnings

## How it works without touching any game file

Valheim is a Unity game. Its entire logic lives in one file:
`valheim_Data\Managed\assembly_valheim.dll`

We never open that file. We never change it. Here is what actually happens:

```
Steam launches Valheim
        │
        ▼
BepInEx\core\BepInEx.dll is injected (via a doorstop shim in the game root)
        │
        ▼
BepInEx scans BepInEx\plugins\*.dll
        │
        ▼
Our DLL is loaded into the same process as the game
        │
        ▼
Our code runs inside the game's memory, sharing all its types
```

BepInEx uses a Unity "doorstop" — a tiny native DLL called `winhttp.dll` placed
next to the game executable. Windows loads it automatically when the game starts,
and it redirects execution to BepInEx before Unity's own code runs. The game file
itself is untouched.

---

## Key technology: Harmony patching

Harmony (0Harmony.dll) lets you attach your own code **before or after** any
method in the game without recompiling or editing the original binary. It works
by rewriting the compiled method in memory at runtime (a technique called
JIT trampolining).

```csharp
[HarmonyPatch(typeof(Terminal), nameof(Terminal.Awake))]
public static class TerminalPatch
{
    static void Postfix()   // runs AFTER Terminal.Awake finishes
    {
        // register our custom console commands here
    }
}
```

Three patch types:
| Type | Runs | Use for |
|------|------|---------|
| `Prefix` | Before the original method | Override or skip the original |
| `Postfix` | After the original method | Add behaviour after the fact |
| `Transpiler` | Rewrites IL bytecode | Surgical edits inside the method |

We only used Prefix and Postfix. Transpilers exist but are complex.

---

## Key technology: Reflection

The game's DLL is compiled C#. Even `private` fields and methods exist in the
binary and are readable at runtime via `System.Reflection`. We used this heavily
because Valheim marks most of its internals private.

```csharp
// Read a private field
FieldInfo f = typeof(TerrainComp).GetField(
    "m_levelDelta", BindingFlags.Instance | BindingFlags.NonPublic);
float[] arr = (float[])f.GetValue(comp);

// Call a private method
MethodInfo m = typeof(TerrainComp).GetMethod(
    "DoOperation", BindingFlags.Instance | BindingFlags.NonPublic);
m.Invoke(comp, new object[] { center, settings });
```

Reflection is slower than a direct call, but for console commands and
one-shot terrain ops the performance cost is irrelevant.

---

## Key technology: ZDO — Valheim's save system

Every placed object in the world (torch, chest, terrain chunk…) has a **ZDO**
(Zoned Data Object). The ZDO is serialised to disk when the world saves and
replicated to clients in multiplayer. It is the canonical persistent state of
an object.

We store our custom data (torch color, brightness) directly in the ZDO:

```csharp
var zdo = nview.GetZDO();
zdo.Set("tc_hex",  "#FF4400");   // survives world save/reload
zdo.Set("tc_mult", 1.5f);

// Reading it back on load
string hex  = zdo.GetString("tc_hex",  "");
float  mult = zdo.GetFloat ("tc_mult", 1f);
```

ZDO supports: `string`, `float`, `int`, `bool`, `long`, `Vector3`,
`Quaternion`, `byte[]`.

---

## Project structure

```
CustomValheim\
├── custom_valheim_intro.md        ← this file
│
├── TerrainLeveler\                ← Mod 1: terrain manipulation
│   ├── TerrainLeveler.csproj
│   └── Plugin.cs
│
└── TorchColor\                    ← Mod 2: torch color/brightness
    ├── TorchColor.csproj
    └── Plugin.cs
```

Both mods share the same pattern:
1. Reference the game DLLs as read-only (never modify them)
2. Define a `BaseUnityPlugin` class (BepInEx entry point)
3. Call `_harmony.PatchAll()` in `Awake()` to activate all `[HarmonyPatch]` classes
4. Register console commands in a `Terminal.Awake` postfix

---

## Mod 1 — TerrainLeveler

**Commands:** `levelterrain <radius> [offset]` · `undoterrain`

### What we learned

**Problem 1 — Wrong patch target**
`Terminal.InitTerminal` does not exist in this version of Valheim. The correct
hook is `Terminal.Awake`. Always reflect on the assembly before assuming a
method name:
```powershell
$asm.GetType("Terminal").GetMethods() | Select-Object Name | Sort-Object Name
```

**Problem 2 — Command did nothing**
`TerrainComp.LevelTerrain` is a private *math helper*. It modifies in-memory
arrays but never saves. The full pipeline that actually changes the terrain is:
```
DoOperation(Vector3 pos, TerrainOp.Settings settings)  ← runs the levelling math
Save()                                                  ← writes result to ZDO
```
Both are private, so both are called via reflection.

**Problem 3 — C# version**
`net472` defaults to C# 7.3. Static local functions (`static T Foo()` inside a
method) require C# 8.0+. Workaround: make them regular private static methods
on the class.

### Undo mechanism
Before each `levelterrain` call, we deep-copy the raw arrays from every
affected `TerrainComp` into a `TerrainSnapshot`:
- `m_modifiedHeight` (bool[])
- `m_levelDelta` (float[])
- `m_smoothDelta` (float[])
- `m_modifiedPaint` (bool[])
- `m_paintMask` (Color[])

These snapshots are pushed onto a `Stack<List<TerrainSnapshot>>`. `undoterrain`
pops the top entry, writes the arrays back, and calls `Save()`.

### Negative offset (digging)
`TerrainOp.Settings.m_levelOffset` accepts negative values. Passing `-3` digs
a pit 3 units below your feet.

---

## Mod 2 — TorchColor

**Commands:** `torchcolor <#RRGGBB | reset> [brightness]`

### What we learned

**Finding the hovered object**
Rolling your own `Physics.Raycast` with no layer mask hits invisible colliders
(terrain, player, triggers) that are not part of the torch's GameObject hierarchy.
Use `Player.GetHoverObject()` instead — it is the same result the game already
computes every frame with the correct layers and interaction range.

**The torch has three flame GameObjects**
`Fireplace` exposes three named child objects:
- `m_enabledObjectHigh` — full-fuel flame
- `m_enabledObject` — normal flame
- `m_enabledObjectLow` — low-fuel flame

Searching from the Fireplace root also picks up the pole, base collider, and
ground-level lights. Always search *inside* these three objects only.

**The flame is a ParticleSystem, not just a Light**
The orange fire you see is rendered by `UnityEngine.ParticleSystem`. Changing
only the `Light` color makes the *glow on the ground* change but leaves the
flame sprite itself orange. You must also set `ps.main.startColor` for each
`ParticleSystem` under the flame objects.

**Persisting color across reloads**
`Fireplace.Awake` is patched to read `tc_hex` / `tc_mult` from the ZDO and
re-apply the color. `Fireplace.UpdateState` (private, patched with a string
literal instead of `nameof`) is also patched because Valheim re-activates one
of the three flame objects when fuel level changes, which resets the particle
colors.

**Private vs public methods**
`nameof(Fireplace.UpdateState)` is a compile-time expression — it fails to
compile when the member is private. Use a string literal `"UpdateState"` in
the `[HarmonyPatch]` attribute instead.

---

## Reference — useful PowerShell snippet for exploring any type

```powershell
$asm = [Reflection.Assembly]::LoadFrom(
    "F:\SteamLibrary\steamapps\common\Valheim\valheim_Data\Managed\assembly_valheim.dll")

# List all types
$asm.GetTypes() | Select-Object -ExpandProperty Name | Sort-Object

# Inspect a type (binding flags: 60 = Instance|Static|Public|NonPublic)
$t = $asm.GetType("Fireplace")
$t.GetFields(60)  | ForEach-Object { $_.FieldType.Name + "  " + $_.Name }
$t.GetMethods(60) | Select-Object -ExpandProperty Name | Sort-Object -Unique

# Check visibility of a method
$m = $t.GetMethod("UpdateState", 60)
"IsPublic=$($m.IsPublic)  IsPrivate=$($m.IsPrivate)"

# Get method parameters
$m.GetParameters() | Select-Object Name, ParameterType
```

---

## Build & deploy

```powershell
# Build a mod
cd f:\Projects\CustomValheim\TorchColor
dotnet build TorchColor.csproj -c Release

# Output DLL
bin\Release\net472\ValheimTorchColor.dll

# Deploy (copy to plugins folder)
Copy-Item bin\Release\net472\ValheimTorchColor.dll `
    "F:\SteamLibrary\steamapps\common\Valheim\BepInEx\plugins\"
```

The `.csproj` references game DLLs with `<Private>false</Private>` so they are
**not** bundled into your output — only your own code ships in the DLL.

---

## Game files we touched: none

| File / folder | Status |
|---|---|
| `valheim.exe` | Untouched |
| `assembly_valheim.dll` | Untouched (read-only reference) |
| `BepInEx\core\*.dll` | Untouched |
| `BepInEx\plugins\ValheimTerrainLeveler.dll` | **Added by us** |
| `BepInEx\plugins\ValheimTorchColor.dll` | **Added by us** |

To fully uninstall either mod: delete the DLL from `plugins\`. The game returns
to vanilla instantly. World saves with custom torch colors will just ignore the
unknown ZDO keys and display the default orange flame again.
