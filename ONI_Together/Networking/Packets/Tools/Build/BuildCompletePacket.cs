using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Tools;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shared.Profiling;
using UnityEngine;
using ONI_Together.Networking.Components;
using Rendering;

namespace ONI_Together.Networking.Packets.Tools.Build
{
    public class BuildCompletePacket : IPacket
    {
        private const int MaxMaterialTagCount = 64;

        public int Cell;
        public string PrefabID;
        public Orientation Orientation;
        public List<string> MaterialTags = new List<string>();
        public float Temperature;
        public string FacadeID = "DEFAULT_FACADE";
        public int WorkerNetId;

        // Utility buildings
        public UtilityConnections UtilityConnectionFlags;

        public ObjectLayer ObjectLayer;

        /// <summary>The ghost sat on def.ReplacementLayer and replaces the complete building on ObjectLayer (a tile, wire or pipe upgrade).</summary>
        public bool IsReplacementTile;

        public void Serialize(BinaryWriter writer)
        {
            using var _ = Profiler.Scope();

            writer.Write(Cell);
            writer.Write(PrefabID);
            writer.Write((int)Orientation);
            writer.Write(Temperature);
            writer.Write(FacadeID);

            writer.Write(MaterialTags.Count);
            foreach (var tag in MaterialTags)
                writer.Write(tag);

            // Write connection flags
            writer.Write((int)UtilityConnectionFlags);

            writer.Write((int)ObjectLayer);

            writer.Write(WorkerNetId);
            writer.Write(IsReplacementTile);
        }

        public void Deserialize(BinaryReader reader)
        {
            using var _ = Profiler.Scope();

            Cell = reader.ReadInt32();
            PrefabID = reader.ReadString();
            Orientation = (Orientation)reader.ReadInt32();
            Temperature = reader.ReadSingle();
            FacadeID = reader.ReadString();

            int count = reader.ReadInt32();
            if (count < 0 || count > MaxMaterialTagCount)
            {
                DebugConsole.LogWarning($"[BuildCompletePacket] Invalid material tag count: {count}");
                Cell = Grid.InvalidCell;
                MaterialTags = [];
                return;
            }
            MaterialTags = new List<string>(count);
            for (int i = 0; i < count; i++)
                MaterialTags.Add(reader.ReadString());

            UtilityConnectionFlags = (UtilityConnections)reader.ReadInt32();
            ObjectLayer = (ObjectLayer)reader.ReadInt32();

            WorkerNetId = reader.ReadInt32();
            IsReplacementTile = reader.ReadBoolean();
        }

        public void OnDispatched()
        {
            using var _ = Profiler.Scope();

            if (!Grid.IsValidCell(Cell))
            {
                DebugConsole.LogWarning($"[BuildCompletePacket] Invalid cell: {Cell}");
                return;
            }

            var def = Assets.GetBuildingDef(PrefabID);
            if (def == null)
            {
                DebugConsole.LogWarning($"[BuildCompletePacket] Unknown building def: {PrefabID}");
                return;
			}

            // A rocket module is not a world building and cannot be raised by def.Build.
            //
            // Its components read the craft they belong to, and the craft is what fills that
            // in: RocketModuleCluster._craftInterface is only ever written by the setter the
            // Clustercraft assembly calls. Built standalone at a cell, nothing calls it, and
            // spawning the module kills the session:
            //
            //   ModuleGenerator.OnSpawn        NullReferenceException at IL 0x13
            //     clustercraft = CraftInterface.GetComponent<Clustercraft>()   // null deref
            //   ReorderableBuilding.OnSpawn    NullReferenceException at IL 0x137
            //
            // Two separate components, which is why this is refused here rather than guarded
            // on either of them - the object itself is the thing that must not exist, and
            // guarding one component only moves the crash to the next.
            //
            // The client goes without the module until the next hard sync, which loads the
            // save and assembles the craft properly. That is a real gap, but the alternative
            // is what the log shows: the module appears only as a broken object and the game
            // drops to its error screen.
            if (def.BuildingComplete != null && def.BuildingComplete.GetComponent<RocketModuleCluster>() != null)
            {
                DebugConsole.LogWarning($"[BuildCompletePacket] {PrefabID} is a rocket module, not a world building - skipping build at cell {Cell}");
                return;
            }

			var tags = MaterialTags.Select(t => new Tag(t)).ToList();

            if (tags.Count == 0)
            {
                DebugConsole.LogWarning($"[BuildCompletePacket] No materials provided for {PrefabID} at cell {Cell}, using SandStone as fallback.");
                tags.Add(SimHashes.SandStone.CreateTag());
            }


			using var scope = OrderApplyScope.Enter();

            int objectLayer = (int)ObjectLayer;
            int ghostLayer = IsReplacementTile && def.ReplacementLayer != ObjectLayer.NumLayers ? (int)def.ReplacementLayer : objectLayer;

            // The ghost this completion finishes: the constructable of this def. For a
            // tile, wire or pipe upgrade it sits on the replacement layer while the old
            // complete building keeps the building layer.
            GameObject ghost = SameDefConstructable(Grid.Objects[Cell, ghostLayer], def);
            if (ghost == null && def.ReplacementLayer != ObjectLayer.NumLayers && ghostLayer != (int)def.ReplacementLayer)
            {
                // The host's flag says plain build, but this side queued it as a replacement
                // (the tile here was still standing when the order arrived).
                ghost = SameDefConstructable(Grid.Objects[Cell, (int)def.ReplacementLayer], def);
                if (ghost != null) ghostLayer = (int)def.ReplacementLayer;
            }
            if (ghost == null && IsBridge(def))
            {
                // A two-cell bridge is registered at one of its cells; only its own ghost
                // counts, never whatever building the neighbour cell holds.
                bool vertical = Orientation == Orientation.R90 || Orientation == Orientation.R270;
                int first = vertical ? Grid.CellAbove(Cell) : Grid.CellLeft(Cell);
                int second = vertical ? Grid.CellBelow(Cell) : Grid.CellRight(Cell);
                ghost = SameDefConstructable(Grid.IsValidCell(first) ? Grid.Objects[first, ghostLayer] : null, def)
                     ?? SameDefConstructable(Grid.IsValidCell(second) ? Grid.Objects[second, ghostLayer] : null, def);
            }

            GameObject standing = Grid.Objects[Cell, objectLayer];
            bool standingIsComplete = standing != null && standing != ghost && standing.GetComponent<BuildingComplete>() != null;
            bool sameDefStanding = standingIsComplete && standing.TryGetComponent<Building>(out var standingBuilding) && standingBuilding.Def == def;

            // Already finished here (own finalization or an earlier packet): rebuilding
            // would stack a copy.
            if (sameDefStanding && ghost == null && !IsReplacementTile)
            {
                DebugConsole.LogAggregated("BuildComplete.AlreadyStanding", $"[BuildCompletePacket] {PrefabID} already complete at cell {Cell}");
                return;
            }

            if (ghost == null)
            {
                // No ghost: the BuildPacket never arrived or was refused, so the building
                // would simply be missing on this client. Raise it outright - after the
                // placement checks the game does, because def.Build itself does none and
                // would put a building inside rock or on top of another.
                Vector3 pos = Grid.CellToPosCBC(Cell, Grid.SceneLayer.Building);
                bool replaceTile = standingIsComplete;
                if (!def.IsValidBuildLocation(null, pos, Orientation, replaceTile) || !def.IsValidPlaceLocation(null, pos, Orientation, replaceTile, out string reason))
                {
                    DebugConsole.LogWarning($"[BuildCompletePacket] No constructable for {PrefabID} at cell {Cell} and the cell is not a valid location here (solid={Grid.Solid[Cell]}, standing={(standing != null ? standing.name : "nothing")}); not built");
                    return;
                }
                DebugConsole.LogWarning($"[BuildCompletePacket] No constructable for {PrefabID} at cell {Cell} (layer {ghostLayer}); building it directly");
            }
            else
            {
                // The ghost is not registered anywhere that matters; its deferred cleanup
                // is harmless. The DeleteObject lifecycle removes its automation port
                // visualizers from LogicCircuitManager.
                int ghostCell = Grid.PosToCell(ghost);
                ghost.DeleteObject();
                if (Grid.IsValidCell(ghostCell) && Grid.Objects[ghostCell, ghostLayer] == ghost)
                    Grid.Objects[ghostCell, ghostLayer] = null;
                if (Grid.Objects[Cell, ghostLayer] == ghost)
                    Grid.Objects[Cell, ghostLayer] = null;
            }

            if (standingIsComplete)
            {
                // A complete building must not share the cell with its replacement for
                // even a frame: Destroy is deferred, and the old wire's or pipe's cleanup
                // unregisters its cell from the utility network AFTER the new one
                // registered, which left the new item disconnected ("Cell N already has a
                // utility network connector assigned", dead wires). The old one goes now
                // and the new one is raised on the next frame, when the cleanup is done.
                if (standing.TryGetComponent<Conduit>(out var conduit))
                    conduit.GetFlowManager()?.MarkForReplacement(Cell);
                standing.DeleteObject();
                if (Grid.Objects[Cell, objectLayer] == standing)
                    Grid.Objects[Cell, objectLayer] = null;
                var capturedDef = def;
                var capturedTags = tags;
                GameScheduler.Instance.ScheduleNextFrame("ONI_Together.BuildComplete", _ =>
                {
                    using var deferredScope = OrderApplyScope.Enter();
                    Build(capturedDef, capturedTags, objectLayer, " (replacement)");
                });
                return;
            }

            Build(def, tags, objectLayer, string.Empty);
        }

        private static bool IsBridge(BuildingDef def)
        {
            var complete = def.BuildingComplete;
            return complete.GetComponent<ConduitBridgeBase>() || complete.GetComponent<WireUtilityNetworkLink>()
                || complete.GetComponent<LogicUtilityNetworkLink>() || def.PrefabID == ContactConductivePipeBridgeConfig.ID;
        }

        private static GameObject SameDefConstructable(GameObject go, BuildingDef def)
        {
            return go != null && go.GetComponent<Constructable>() != null
                && go.TryGetComponent<Building>(out var building) && building.Def == def ? go : null;
        }

        private void Build(BuildingDef def, List<Tag> tags, int objectLayer, string note)
        {
            float temperature = Temperature;
            if (temperature <= 1f || float.IsNaN(temperature) || float.IsInfinity(temperature))
                temperature = Mathf.Min(def.Temperature, ElementLoader.GetMinMeltingPointAmongElements(tags) - 10f);

            var builtObj = def.Build(
                Cell,
                Orientation,
                null,
                tags,
                temperature,
                FacadeID,
                playsound: false,
                GameClock.Instance.GetTime()
            );

            // Apply wire/pipe connections for utility buildings
            if (builtObj != null && (int)UtilityConnectionFlags != 0)
            {
                ApplyUtilityConnections(builtObj, def);
            }

            if (builtObj != null)
                DebugConsole.Log($"[BuildCompletePacket] Finalized {PrefabID} at cell {Cell}{note}");
            else
                DebugConsole.LogWarning($"[BuildCompletePacket] def.Build returned nothing for {PrefabID} at cell {Cell}{note} (solid={Grid.Solid[Cell]}, layer holds {(Grid.Objects[Cell, objectLayer] != null ? Grid.Objects[Cell, objectLayer].name : "nothing")})");
        }

        private void ApplyUtilityConnections(GameObject go, BuildingDef def)
        {
            // Neighbours are baked into the conduit managers
            if (go.TryGetComponent<KAnimGraphTileVisualizer>(out var vis))
            {
                vis.UpdateConnections(UtilityConnectionFlags);
                vis.Refresh();
            }
        }

        private void ApplyUtilityConnections(KAnimGraphTileVisualizer vis, UtilityConnections flags)
        {
            vis.UpdateConnections(flags);
            vis.Refresh();
        }
    }
}

