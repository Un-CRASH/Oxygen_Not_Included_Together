using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Tools;
using Steamworks;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shared.Profiling;
using UnityEngine;
using static PathFinder;
using static STRINGS.MISC;

namespace ONI_Together.Networking.Packets.Tools.Build
{
    public class BuildPacket : IPacket
    {
        private const int MaxMaterialTagCount = 64;

        private string PrefabID;
        private int Cell;
        private Orientation Orientation;
        private List<string> MaterialTags = new List<string>();
        private PrioritySetting Priority;
        private ObjectLayer ObjectLayer;
        private bool InstantBuild;

        public BuildPacket()
        {
        }

        public BuildPacket(string prefabID, int cell, Orientation orientation, IEnumerable<Tag> materials, ObjectLayer objectLayer, bool instantBuild = false)
        {
            using var _ = Profiler.Scope();

            PrefabID = prefabID;
            Cell = cell;
            Orientation = orientation;
            MaterialTags = materials.Select(t => t.ToString()).ToList();
            InstantBuild = instantBuild;

            // The build menu's priority widget only exists while the menu is open; a
            // placement made from code (tests, other mods) must not fail on it.
            try
            {
                if (PlanScreen.Instance)
                    Priority = PlanScreen.Instance.GetBuildingPriority();
            }
            catch (System.NullReferenceException)
            {
                Priority = new PrioritySetting(PriorityScreen.PriorityClass.basic, 5);
            }

            ObjectLayer = objectLayer;
        }

        public void Serialize(BinaryWriter writer)
        {
            using var _ = Profiler.Scope();

            writer.Write(PrefabID);
            writer.Write(Cell);
            writer.Write((int)Orientation);
            writer.Write(MaterialTags.Count);
            foreach (var tag in MaterialTags)
                writer.Write(tag);

            writer.Write((int)Priority.priority_class);
            writer.Write(Priority.priority_value);

            writer.Write((int)ObjectLayer);
            writer.Write(InstantBuild);
        }

        public void Deserialize(BinaryReader reader)
        {
            using var _ = Profiler.Scope();

            PrefabID = reader.ReadString();
            Cell = reader.ReadInt32();
            Orientation = (Orientation)reader.ReadInt32();
            int count = reader.ReadInt32();
            if (count < 0 || count > MaxMaterialTagCount)
            {
                DebugConsole.LogWarning($"[BuildPacket] Invalid material tag count: {count}");
                Cell = Grid.InvalidCell;
                MaterialTags = [];
                return;
            }
            MaterialTags = new List<string>();
            for (int i = 0; i < count; i++)
                MaterialTags.Add(reader.ReadString());

            Priority = new PrioritySetting((PriorityScreen.PriorityClass)reader.ReadInt32(), reader.ReadInt32());
            ObjectLayer = (ObjectLayer)reader.ReadInt32();
            InstantBuild = reader.ReadBoolean();
        }

        public void OnDispatched()
        {
            using var _ = Profiler.Scope();

            if (!Grid.IsValidCell(Cell))
            {
                DebugConsole.LogWarning($"[BuildPacket] Invalid cell: {Cell}");
                return;
            }

            var def = Assets.GetBuildingDef(PrefabID);
            if (def == null)
            {
                DebugConsole.LogWarning($"[BuildPacket] Unknown building def: {PrefabID}");
                return;
            }

                        if (IsRepeat(def))
                            return;

                        // The placement and its priority are one remote order; nothing in here
                        // may echo back as a packet of its own.
                        using var scope = OrderApplyScope.Enter();

                        var selected_elements = MaterialTags.Select(t => TagManager.Create(t)).ToList();
                        Vector3 pos = Grid.CellToPosCBC(Cell, Grid.SceneLayer.Building);

                        GameObject builtItem = null;
                        // The same building already stands here: the only thing left to do is a
                        // material replacement, which the replacement path decides on its own
                        // (same material -> nothing). Queueing a fresh copy would stack it.
                        if (!IsSameDefStanding(def))
                        {
                            if (InstantBuild)
                                builtItem = InstantBuildBuilding(def, selected_elements, pos);
                            else
                                builtItem = QueueBuild(def, selected_elements, pos);
                        }

                        if (builtItem == null && def.ReplacementLayer != ObjectLayer.NumLayers)
                            builtItem = HandleReplacementInstant(def, pos, selected_elements) ?? HandleReplacementQueued(def, pos, selected_elements);

                        SetPriority(builtItem);
                        if (builtItem != null)
                            DebugConsole.Log($"[BuildPacket] Built item {def.PrefabID} at cell {Cell}{(_attempt > 0 ? $" (attempt {_attempt + 1})" : string.Empty)}");
                        else if (ScheduleRetry())
                            DebugConsole.LogAggregated("BuildPacket.Retry", $"[BuildPacket] Nothing to build for {def.PrefabID} at cell {Cell} yet (layer holds {Describe(Grid.Objects[Cell, (int)def.ObjectLayer])}, solid={Grid.Solid[Cell]}); trying again");
                        else
                            DebugConsole.LogWarning($"[BuildPacket] Nothing to build for {def.PrefabID} at cell {Cell} after {RetryDelays.Length} attempts: layer holds {Describe(Grid.Objects[Cell, (int)def.ObjectLayer])}, solid={Grid.Solid[Cell]}");
                    }

                    /// <summary>
                    /// A placement the receiver's world refuses right now is tried again later,
                    /// not dropped. The two cases seen in the logs: a cancel-then-re-place at
                    /// one cell applied in one frame, where the game's deferred destroy leaves
                    /// the dead ghost in Grid.Objects until the end of the frame; and a client's
                    /// order on a cell the host's world makes valid a little later (a ladder
                    /// over a dig that had not completed here). On the host a refused client
                    /// placement was lost for good - BuildComplete is host-only - so the client
                    /// kept a ghost nobody would ever build.
                    /// </summary>
                    private static readonly float[] RetryDelays = { 0f, 2f, 10f, 30f };
                    private int _attempt;

                    private bool ScheduleRetry()
                    {
                        if (_attempt >= RetryDelays.Length || GameScheduler.Instance == null)
                            return false;
                        float delay = RetryDelays[_attempt++];
                        if (delay <= 0f)
                            GameScheduler.Instance.ScheduleNextFrame("ONI_Together.BuildRetry", _ => Retry());
                        else
                            GameScheduler.Instance.Schedule("ONI_Together.BuildRetry", delay, _ => Retry());
                        return true;
                    }

                    private void Retry()
                    {
                        if (!MultiplayerSession.InActiveSession)
                            return;
                        OnDispatched();
                    }

                    private static string Describe(GameObject go)
                    {
                        if (go == null) return "nothing";
                        string state = go.GetComponent<Constructable>() != null ? "under construction" : go.GetComponent<BuildingComplete>() != null ? "complete" : "other";
                        return $"{go.name} ({state})";
                    }

                    private static int _frame = -1;
                    private static readonly HashSet<(int, string, int, int)> _appliedThisFrame = new HashSet<(int, string, int, int)>();

                    /// <summary>
                    /// A second order for the same building at the same cell is a repeat, never a
                    /// wish for two of them. Until BuildToolPatch learned to send one packet per
                    /// placement, a client sent one per mouse move, and the host handled the batch
                    /// in a single frame: the log shows twelve "Built item Tile" with one timestamp
                    /// and six FarmTiles finalized at one cell.
                    ///
                    /// Two checks, because a building with a Rotatable marks Grid.Objects only in
                    /// Constructable.OnSpawn, a frame after it was placed:
                    /// - the same (cell, prefab, layer) already applied in this frame;
                    /// - the same def already under construction on the building layer or, for a
                    ///   tile replacement, on the replacement layer.
                    /// </summary>
                    private bool IsRepeat(BuildingDef def)
                    {
                        if (Time.frameCount != _frame)
                        {
                            _frame = Time.frameCount;
                            _appliedThisFrame.Clear();
                        }

                        if (!_appliedThisFrame.Add((Cell, PrefabID, (int)def.ObjectLayer, (int)Orientation)))
                        {
                            DebugConsole.Log($"[BuildPacket] Repeat of {def.PrefabID} at cell {Cell} in the same frame - skipped");
                            return true;
                        }

                        if (IsSameDefUnderConstruction(Grid.Objects[Cell, (int)def.ObjectLayer], def) ||
                            (def.ReplacementLayer != ObjectLayer.NumLayers && IsSameDefUnderConstruction(Grid.Objects[Cell, (int)def.ReplacementLayer], def)))
                        {
                            DebugConsole.Log($"[BuildPacket] {def.PrefabID} is already queued at cell {Cell} - skipped");
                            return true;
                        }

                        return false;
                    }

                    private static bool IsSameDefUnderConstruction(GameObject go, BuildingDef def)
                    {
                        return go != null &&
                               go.TryGetComponent<Building>(out var building) && building != null && building.Def == def &&
                               go.GetComponent<Constructable>() != null;
                    }

                    private bool IsSameDefStanding(BuildingDef def)
                    {
                        GameObject go = Grid.Objects[Cell, (int)def.ObjectLayer];
                        return go != null &&
                               go.TryGetComponent<Building>(out var building) && building != null && building.Def == def &&
                               go.GetComponent<BuildingComplete>() != null;
                    }

        private GameObject QueueBuild(BuildingDef def, List<Tag> selected_elements, Vector3 pos)
        {
            GameObject visualizer = Util.KInstantiate(def.BuildingPreview, pos);
            return def.TryPlace(visualizer, pos, Orientation, selected_elements, "DEFAULT_FACADE");
        }

        private GameObject HandleReplacementQueued(BuildingDef def, Vector3 pos, List<Tag> selected_elements)
        {
            GameObject replacementCandidate = def.GetReplacementCandidate(Cell);
            if (replacementCandidate == null || def.IsReplacementLayerOccupied(Cell))
                return null;

            BuildingComplete component = replacementCandidate.GetComponent<BuildingComplete>();
            if (component == null || !component.Def.Replaceable || !def.CanReplace(replacementCandidate))
                return null;

            Tag tag = replacementCandidate.GetComponent<PrimaryElement>().Element.tag;
            if (tag.GetHash() == (int)SimHashes.StableSnow)
                tag = SimHashes.Snow.CreateTag();
            if (component.Def == def && selected_elements[0] == tag)
                return null;

            GameObject visualizer = Util.KInstantiate(def.BuildingPreview, pos);
            GameObject builtItem = def.TryReplaceTile(visualizer, pos, Orientation, selected_elements, "DEFAULT_FACADE");
            Grid.Objects[Cell, (int)def.ReplacementLayer] = builtItem;
            return builtItem;
        }

        private GameObject InstantBuildBuilding(BuildingDef def, List<Tag> selected_elements, Vector3 pos)
        {
            if (!def.IsValidBuildLocation(null, pos, Orientation) || !def.IsValidPlaceLocation(null, pos, Orientation, out _))
                return null;

            if (def.ObjectLayer == ObjectLayer.Building)
            {
                def.RunOnArea(Cell, Orientation, offset_cell =>
                {
                    if (Uprootable.CanUproot(Grid.Objects[offset_cell, (int)def.ObjectLayer], out var uprootable))
                        uprootable.CompleteWork(null);
                });
            }
            else if (def.ObjectLayer == ObjectLayer.Backwall)
            {
                def.RunOnArea(Cell, Orientation, offset_cell =>
                {
                    if (BackwallManager.HasBackwall(offset_cell))
                        SimMessages.Dig(offset_cell, -1, skipEvent: true, backwall: true);
                });
            }

            float temp = Mathf.Min(def.Temperature, ElementLoader.GetMinMeltingPointAmongElements(selected_elements) - 10f);
            return def.Build(Cell, Orientation, null, selected_elements, temp, "DEFAULT_FACADE", playsound: false, GameClock.Instance.GetTime());
        }

        private GameObject HandleReplacementInstant(BuildingDef def, Vector3 pos, List<Tag> selected_elements)
        {
            if (!InstantBuild)
                return null;

            GameObject replacementCandidate = def.GetReplacementCandidate(Cell);
            if (replacementCandidate == null || def.IsReplacementLayerOccupied(Cell))
                return null;

            BuildingComplete component = replacementCandidate.GetComponent<BuildingComplete>();
            if (component == null || !component.Def.Replaceable || !def.CanReplace(replacementCandidate))
                return null;

            Tag tag = replacementCandidate.GetComponent<PrimaryElement>().Element.tag;
            if (tag.GetHash() == (int)SimHashes.StableSnow)
                tag = SimHashes.Snow.CreateTag();
            if (component.Def == def && selected_elements[0] == tag)
                return null;

            if (!def.IsValidBuildLocation(null, pos, Orientation, replace_tile: true) ||
                !def.IsValidPlaceLocation(null, pos, Orientation, replace_tile: true, out _))
                return null;

            float temp = Mathf.Min(def.Temperature, ElementLoader.GetMinMeltingPointAmongElements(selected_elements) - 10f);
            return def.Build(Cell, Orientation, null, selected_elements, temp, "DEFAULT_FACADE", playsound: false, GameClock.Instance.GetTime());
        }

        private void SetPriority(GameObject gameObject)
        {
            if (gameObject == null)
                return;

            Prioritizable prioritizable = gameObject?.GetComponent<Prioritizable>();
            prioritizable?.SetMasterPriority(Priority);
        }

    }
}
