using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValheimTerrainLeveler
{
    [BepInPlugin("com.yourname.terrainleveler", "TerrainLeveler", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        private readonly Harmony _harmony = new Harmony("com.yourname.terrainleveler");

        private void Awake()
        {
            Log = Logger;
            _harmony.PatchAll();
            Log.LogInfo("TerrainLeveler loaded — 'levelterrain <radius> [offset]' / 'undoterrain'");
        }
    }

    // Stores one complete snapshot of a TerrainComp's height + paint data.
    internal class TerrainSnapshot
    {
        public TerrainComp Comp;
        public bool[]   ModifiedHeight;
        public float[]  LevelDelta;
        public float[]  SmoothDelta;
        public bool[]   ModifiedPaint;
        public Color[]  PaintMask;
    }

    [HarmonyPatch(typeof(Terminal), nameof(Terminal.Awake))]
    public static class TerminalPatch
    {
        private static bool _registered;

        // Each entry is the set of snapshots captured before one 'levelterrain' call.
        private static readonly Stack<List<TerrainSnapshot>> _undoStack = new Stack<List<TerrainSnapshot>>();

        private static readonly FieldInfo _fModHeight  = typeof(TerrainComp).GetField("m_modifiedHeight", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo _fLevelDelta = typeof(TerrainComp).GetField("m_levelDelta",     BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo _fSmoothDelta= typeof(TerrainComp).GetField("m_smoothDelta",    BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo _fModPaint   = typeof(TerrainComp).GetField("m_modifiedPaint",  BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo _fPaintMask  = typeof(TerrainComp).GetField("m_paintMask",      BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly MethodInfo _doOp  = typeof(TerrainComp).GetMethod("DoOperation", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo _saveOp = typeof(TerrainComp).GetMethod("Save",       BindingFlags.Instance | BindingFlags.NonPublic);

        private static TerrainSnapshot Snapshot(TerrainComp comp)
        {
            return new TerrainSnapshot
            {
                Comp           = comp,
                ModifiedHeight = CloneArray<bool>  (_fModHeight,   comp),
                LevelDelta     = CloneArray<float> (_fLevelDelta,  comp),
                SmoothDelta    = CloneArray<float> (_fSmoothDelta, comp),
                ModifiedPaint  = CloneArray<bool>  (_fModPaint,    comp),
                PaintMask      = CloneArray<Color> (_fPaintMask,   comp),
            };
        }

        private static T[] CloneArray<T>(FieldInfo f, TerrainComp c)
        {
            var arr = (T[])f.GetValue(c);
            if (arr == null) return null;
            var copy = new T[arr.Length];
            arr.CopyTo(copy, 0);
            return copy;
        }

        private static void Restore(TerrainSnapshot snap)
        {
            _fModHeight.SetValue  (snap.Comp, snap.ModifiedHeight);
            _fLevelDelta.SetValue (snap.Comp, snap.LevelDelta);
            _fSmoothDelta.SetValue(snap.Comp, snap.SmoothDelta);
            _fModPaint.SetValue   (snap.Comp, snap.ModifiedPaint);
            _fPaintMask.SetValue  (snap.Comp, snap.PaintMask);
            _saveOp.Invoke(snap.Comp, null);
        }

        static void Postfix()
        {
            if (_registered) return;
            _registered = true;

            // ── levelterrain <radius> [offset] ──────────────────────────────
            new Terminal.ConsoleCommand(
                "levelterrain",
                "[radius] [offset] — Flattens terrain to standing height ± offset. Negative offset digs down.",
                args =>
                {
                    float radius = 10f;
                    float offset = 0f;

                    if (args.Length > 1 && float.TryParse(args[1], out float r))
                        radius = Mathf.Clamp(r, 1f, 64f);

                    if (args.Length > 2 && float.TryParse(args[2], out float o))
                        offset = Mathf.Clamp(o, -64f, 64f);

                    var player = Player.m_localPlayer;
                    if (player == null) { args.Context.AddString("No player found."); return; }

                    float groundY = player.transform.position.y;
                    if (Physics.Raycast(
                            player.transform.position + Vector3.up,
                            Vector3.down, out RaycastHit hit, 5f,
                            LayerMask.GetMask("terrain")))
                        groundY = hit.point.y;

                    Vector3 center = new Vector3(
                        player.transform.position.x,
                        groundY,
                        player.transform.position.z);

                    var settings = new TerrainOp.Settings
                    {
                        m_level       = true,
                        m_levelRadius = radius,
                        m_levelOffset = offset,
                    };

                    var snapshots = new List<TerrainSnapshot>();
                    int chunks = 0;

                    foreach (var comp in Object.FindObjectsOfType<TerrainComp>())
                    {
                        if (comp == null || !comp.IsOwner()) continue;
                        snapshots.Add(Snapshot(comp));          // save state BEFORE
                        _doOp.Invoke(comp, new object[] { center, settings });
                        _saveOp.Invoke(comp, null);
                        chunks++;
                    }

                    if (snapshots.Count > 0)
                        _undoStack.Push(snapshots);

                    args.Context.AddString(
                        $"Levelled terrain — Y={groundY + offset:F2}, radius={radius}, offset={offset:+0.##;-0.##;0}, chunks={chunks}");
                },
                isCheat: true
            );

            // ── undoterrain ──────────────────────────────────────────────────
            new Terminal.ConsoleCommand(
                "undoterrain",
                "Undoes the last levelterrain operation",
                args =>
                {
                    if (_undoStack.Count == 0)
                    {
                        args.Context.AddString("Nothing to undo.");
                        return;
                    }

                    var snapshots = _undoStack.Pop();
                    int restored = 0;

                    foreach (var snap in snapshots)
                    {
                        if (snap.Comp == null) continue;
                        Restore(snap);
                        restored++;
                    }

                    args.Context.AddString($"Undo complete — restored {restored} chunk(s). {_undoStack.Count} step(s) remaining.");
                },
                isCheat: true
            );
        }
    }
}
