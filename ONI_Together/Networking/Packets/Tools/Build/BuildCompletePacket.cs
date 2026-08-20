using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
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


			bool isBridge = def.BuildingComplete.GetComponent<ConduitBridgeBase>() || def.BuildingComplete.GetComponent<WireUtilityNetworkLink>() || def.BuildingComplete.GetComponent<LogicUtilityNetworkLink>() || PrefabID == ContactConductivePipeBridgeConfig.ID;
			int layerIndex = (int)ObjectLayer;
            // Destroy ghost/constructable if it still exists
            GameObject existing = Grid.Objects[Cell, layerIndex];

            if(existing == null && isBridge)
            {
                bool vertical = Orientation == Orientation.R90 || Orientation == Orientation.R270;
                //todo: account for other width bridges; get the offsets from bridge width instead
                int firstToCheck = vertical ? Grid.CellAbove(Cell) : Grid.CellLeft(Cell);
                int secondToCheck = vertical ? Grid.CellBelow(Cell) : Grid.CellRight(Cell);

				existing = Grid.Objects[firstToCheck, layerIndex];
                if(existing == null)
					existing = Grid.Objects[secondToCheck, layerIndex];
			}

            if (existing != null)
            {
                //if (existing.TryGetComponent<Constructable>(out Constructable con))
                //{
                //    if (NetworkIdentityRegistry.TryGet(WorkerNetId, out var identity) &&
                //       identity.TryGetComponent<WorkerBase>(out var worker))
                //    {
                //        con.initialTemperature = Temperature;
                //        con.SelectedElementsTags = tags;
                //        con.FinishConstruction(UtilityConnectionFlags, worker);
                //    }
                //}
                //else
                {
                    // Clean up using ONI's proper lifecycle to ensure automation port visualizers
                    // are removed from LogicCircuitManager.uiVisElements synchronously.
                    // Object.Destroy() would defer cleanup, leaving stale port entries
                    // that block future building placement.
                    existing.DeleteObject();
                    Grid.Objects[Cell, layerIndex] = null;

                    var builtObj = def.Build(
                        Cell,
                        Orientation,
                        null,
                        tags,
                        Temperature,
                        FacadeID,
                        playsound: false,
                        GameClock.Instance.GetTime()
                    );

                    // Apply wire/pipe connections for utility buildings
                    if (builtObj != null && (int)UtilityConnectionFlags != 0)
                    {
                        ApplyUtilityConnections(builtObj, def);
                    }
                }
            }

            DebugConsole.Log($"[BuildCompletePacket] Finalized {PrefabID} at cell {Cell}");
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

