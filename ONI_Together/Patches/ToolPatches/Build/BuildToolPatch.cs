using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Tools.Build;
using System;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Patches.ToolPatches.Build
{
    /// <summary>
    /// Sends a BuildPacket for what the local BuildTool actually placed - and only that.
    ///
    /// BuildTool.TryBuild runs on every mouse move while the tool is down and returns
    /// at once when the cell and orientation are the ones it last handled
    /// (lastDragCell, IL 0x10-0x26). The old postfix could not see that early return
    /// and sent a packet per call: one FirePole cell produced 86 packets, and the
    /// host, handling the batch in a single frame, queued a stack of identical
    /// constructables that the duplicants then built one on top of another (six
    /// FarmTiles finalized at one cell in one client log).
    ///
    /// A placement is detected two ways, because the game marks Grid.Objects at
    /// different times: BuildingDef.Instantiate is counted while TryBuild is on the
    /// stack (queued builds and tile replacements, whatever the marking time - a
    /// building with a Rotatable only marks in Constructable.OnSpawn), and the cell's
    /// Grid.Objects entries are compared before and after (instant build, which
    /// marks synchronously in BuildingDef.Build).
    /// </summary>
    [HarmonyPatch(typeof(BuildTool), nameof(BuildTool.TryBuild))]
    public static class BuildToolPatch
    {
        public struct Snapshot
        {
            public GameObject OnLayer;
            public GameObject OnReplacementLayer;
        }

        private static bool _inTryBuild;
        private static int _instantiated;

        /// <summary>Counted by BuildingDefInstantiatePatch while TryBuild runs.</summary>
        public static void NoteInstantiated()
        {
            if (_inTryBuild)
                _instantiated++;
        }

        static void Prefix(BuildTool __instance, int cell, out Snapshot __state)
        {
            using var _ = Profiler.Scope();

            __state = default;
            _inTryBuild = true;
            _instantiated = 0;
            try
            {
                var def = __instance != null ? __instance.def : null;
                if (def != null && Grid.IsValidCell(cell))
                    __state = Capture(def, cell);
            }
            catch (Exception ex)
            {
                DebugConsole.LogError($"[BuildToolPatch.Prefix] {ex}");
            }
        }

        static void Finalizer()
        {
            _inTryBuild = false;
        }

        static void Postfix(BuildTool __instance, int cell, Snapshot __state)
        {
            using var _ = Profiler.Scope();

            _inTryBuild = false;
            try
            {
                if (!MultiplayerSession.InActiveSession || __instance == null)
                    return;

                var def = __instance.def;
                var selectedElements = __instance.selectedElements;
                var orientation = __instance.GetBuildingOrientation;

                if (def == null || selectedElements == null || !Grid.IsValidCell(cell))
                    return;

                var after = Capture(def, cell);
                bool gridChanged =
                    (after.OnLayer != null && after.OnLayer != __state.OnLayer) ||
                    (after.OnReplacementLayer != null && after.OnReplacementLayer != __state.OnReplacementLayer);

                if (_instantiated == 0 && !gridChanged)
                    return;

                DebugConsole.Log($"[BuildTool] Placed {def.PrefabID} at cell {cell}");

                bool instantBuild = DebugHandler.InstantBuildMode || (Game.Instance.SandboxModeActive && SandboxToolParameterMenu.instance.settings.InstantBuild);
                var packet = new BuildPacket(
                    def.PrefabID,
                    cell,
                    orientation,
                    selectedElements,
                    def.ObjectLayer,
                    instantBuild
                );

                PacketSender.SendToAllOtherPeers(packet);
            }
            catch (Exception ex)
            {
                DebugConsole.LogError($"[BuildToolPatch.Postfix] {ex}");
            }
        }

        private static Snapshot Capture(BuildingDef def, int cell)
        {
            var snapshot = new Snapshot { OnLayer = Grid.Objects[cell, (int)def.ObjectLayer] };
            if (def.ReplacementLayer != ObjectLayer.NumLayers)
                snapshot.OnReplacementLayer = Grid.Objects[cell, (int)def.ReplacementLayer];
            return snapshot;
        }
    }

    /// <summary>
    /// BuildingDef.Instantiate is the one place both TryPlace and TryReplaceTile create
    /// the under-construction object; counting it tells BuildToolPatch that this
    /// TryBuild call placed something.
    /// </summary>
    [HarmonyPatch(typeof(BuildingDef), nameof(BuildingDef.Instantiate))]
    public static class BuildingDefInstantiatePatch
    {
        static void Postfix(GameObject __result)
        {
            if (__result != null)
                BuildToolPatch.NoteInstantiated();
        }
    }
}
