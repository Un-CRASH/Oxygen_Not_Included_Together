using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using UnityEngine;
using static LogicGateVisualizer;

namespace ONI_Together.Misc
{
    public static class BuildingUtils
    {
        public static bool ValidCell(GameObject visualizer, BuildingDef def, int cell, Orientation orientation)
        {
            if (Grid.IsValidCell(cell)
                && Grid.IsVisible(cell))
            {
                bool IsValidPlaceLocation = def.IsValidPlaceLocation(visualizer, cell, orientation, out string failReason);
                bool IgnorableFailReason =
                    failReason == global::STRINGS.UI.TOOLTIPS.HELP_BUILDLOCATION_WALL
                    || failReason == global::STRINGS.UI.TOOLTIPS.HELP_BUILDLOCATION_CORNER
                    || failReason == global::STRINGS.UI.TOOLTIPS.HELP_BUILDLOCATION_CORNER_FLOOR
                    || (failReason == global::STRINGS.UI.TOOLTIPS.HELP_BUILDLOCATION_BACK_WALL_REQUIRED);

                bool validCell = (IsValidPlaceLocation || IgnorableFailReason);
                bool replacement = false;
                return (validCell || replacement);
            }

            return false;
        }

        public static byte[] EncodeStorageToBytes(Storage storage)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(storage.capacityKg);

            var validItems = new List<GameObject>();
            for (int i = 0; i < storage.items.Count; i++)
            {
                var go = storage.items[i];
                if (go == null) continue;
                var pe = go.GetComponent<PrimaryElement>();
                if (pe == null || pe.Mass <= 0f) continue;
                if (!go.TryGetComponent<KPrefabID>(out _)) continue;
                if (IsEntityNotContents(go)) continue;
                validItems.Add(go);
            }

            writer.Write(validItems.Count);
            foreach (var go in validItems)
            {
                var pe = go.GetComponent<PrimaryElement>();
                var prefabID = go.GetComponent<KPrefabID>();
                writer.Write(prefabID.PrefabTag.GetHashCode());
                writer.Write(pe.Mass);
                writer.Write(pe.Temperature);
                writer.Write(pe.DiseaseIdx);
                writer.Write(pe.DiseaseCount);
                // The item's id, so the receiver keeps the same object for the same item
                // (masses were landing on the wrong stack when matched by prefab alone).
                var identity = go.GetExistingNetIdentity();
                writer.Write(identity != null ? identity.NetId : 0);
            }

            return ms.ToArray();
        }

        public static void EncodeStorageContents(Storage storage, Dictionary<string, Variant> optionalValues, string keyPrefix = "")
        {
            optionalValues[keyPrefix + "stor"] = EncodeStorageToBytes(storage);
        }

        public static void RebuildStorageFromBytes(Storage storage, byte[] data, string diseaseReason = "Multiplayer Sync")
        {
            RebuildFromBlob(storage, data, diseaseReason);
        }

        public static void RebuildStorageFromData(Storage storage, Dictionary<string, Variant> data, string keyPrefix = "", string diseaseReason = "Multiplayer Sync")
        {
            if (storage == null) return;

            if (data.TryGetValue(keyPrefix + "stor", out var blobVar) && blobVar.ByteArray != null)
            {
                RebuildStorageFromBytes(storage, blobVar.ByteArray, diseaseReason);
                return;
            }
            
            DebugConsole.LogError($"[Storage/RebuildStorageFromData] Failed to rebuild storage from data! Key: {keyPrefix + "stor"} not found!");
        }

        // capacityKg + count
        private const int BLOB_HEADER_SIZE = sizeof(float) + sizeof(int);

        private struct BlobEntry
        {
            public int Hash; public float Mass; public float Temperature; public byte DiseaseIdx; public int DiseaseCount; public int NetId; public int Match;
        }

        private static void RebuildFromBlob(Storage storage, byte[] blob, string diseaseReason)
        {
            if (blob == null || blob.Length < BLOB_HEADER_SIZE)
                return;

            using var ms = new MemoryStream(blob);
            using var reader = new BinaryReader(ms);

            float capacityKg = reader.ReadSingle();
            int count = reader.ReadInt32();
            var entries = new List<BlobEntry>(count);
            for (int i = 0; i < count; i++)
            {
                var e = new BlobEntry
                {
                    Hash = reader.ReadInt32(),
                    Mass = reader.ReadSingle(),
                    Temperature = reader.ReadSingle(),
                    DiseaseIdx = reader.ReadByte(),
                    DiseaseCount = reader.ReadInt32(),
                    NetId = reader.ReadInt32(),
                    Match = -1,
                };
                if (e.Mass > 0f) entries.Add(e);
            }

            // Applied as a difference, not as empty-and-refill. The items a client loaded
            // from the save carry the host's ids; deleting them and instantiating fresh
            // copies gave a container's contents new, local ids on every snapshot, so a
            // host packet naming a stored item never resolved here. An item is matched by
            // the host's id first, then by prefab (a copy this side made itself has an id
            // of its own), and gets its mass and temperature set; only what is missing is
            // created and only what is gone is deleted. Suits and animals
            // (IsEntityNotContents) are left alone either way.
            var existing = new List<GameObject>();
            for (int i = 0; i < storage.items.Count; i++)
            {
                var item = storage.items[i];
                if (item == null || item.IsNullOrDestroyed()) continue;
                if (IsEntityNotContents(item)) continue;
                existing.Add(item);
            }
            var matched = new bool[existing.Count];

            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    if (e.Match >= 0) continue;
                    if (pass == 0 && e.NetId == 0) continue;
                    Tag tag = new Tag(e.Hash);
                    for (int j = 0; j < existing.Count; j++)
                    {
                        if (matched[j]) continue;
                        if (pass == 0)
                        {
                            var id = existing[j].GetExistingNetIdentity();
                            if (id == null || id.NetId != e.NetId) continue;
                        }
                        else if (existing[j].PrefabID() != tag) continue;
                        matched[j] = true;
                        e.Match = j;
                        entries[i] = e;
                        break;
                    }
                }
            }

            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.Match >= 0)
                {
                    var kept = existing[e.Match];
                    var pe = kept.GetComponent<PrimaryElement>();
                    if (pe != null)
                    {
                        if (Mathf.Abs(pe.Mass - e.Mass) > 0.0005f) pe.Mass = e.Mass;
                        if (Mathf.Abs(pe.Temperature - e.Temperature) > 0.01f) pe.Temperature = e.Temperature;
                    }
                    AdoptHostId(kept, e.NetId);
                    continue;
                }

                Tag tag = new Tag(e.Hash);
                Element elementByHash = ElementLoader.GetElement(tag);
                if (elementByHash != null)
                {
                    storage.AddElement(elementByHash.id, e.Mass, e.Temperature, e.DiseaseIdx, e.DiseaseCount);
                    continue;
                }

                var prefab = Assets.GetPrefab(tag);
                if (prefab == null) continue;

                var scrapObject = GameUtil.KInstantiate(prefab, storage.transform.position, Grid.SceneLayer.Ore);
                if (scrapObject.TryGetComponent<PrimaryElement>(out var newPe))
                {
                    newPe.Mass = e.Mass;
                    newPe.Temperature = e.Temperature;
                    if (e.DiseaseIdx != byte.MaxValue)
                        newPe.AddDisease(e.DiseaseIdx, e.DiseaseCount, diseaseReason);
                }
                scrapObject.SetActive(true);
                // Stored the way the game restores a save: no merging into a stack that is
                // already there (the host keeps them apart, so the count must match) and
                // with the events on, so the item knows its container (Pickupable.storage),
                // leaves the ground layer and the side screen refreshes. Stored with the
                // events blocked, as before, a rebuilt item stayed a loose item at the
                // container's cell as far as the rest of the game could tell.
                storage.Store(scrapObject, true, false, true, true);
                storage.ApplyStoredItemModifiers(scrapObject, true, false);
                AdoptHostId(scrapObject, e.NetId);
            }

            for (int j = 0; j < existing.Count; j++)
            {
                if (!matched[j])
                    existing[j].DeleteObject();
            }
            storage.items.RemoveAll(item => item == null || item.IsNullOrDestroyed());
        }

        /// <summary>A stored item takes the host's id for it, so host packets about it resolve here; not when another object already answers to that id.</summary>
        private static void AdoptHostId(GameObject go, int netId)
        {
            if (netId == 0 || go == null) return;
            var identity = go.GetExistingNetIdentity();
            if (identity == null || identity.NetId == netId) return;
            if (NetworkIdentityRegistry.TryGet(netId, out var other, logFailure: false) && other != null && other.gameObject != go)
                return;
            identity.OverrideNetId(netId);
        }
        
        /// <summary>
        /// Something with a life of its own, rather than bulk contents.
        ///
        /// This blob describes a container as a list of prefab, mass and temperature,
        /// and it is applied by emptying the container and rebuilding it. That is right
        /// for a pile of dirt and destructive for anything carrying state the blob has
        /// no room for.
        ///
        /// An atmo suit in a locker has an owner, a durability and its own oxygen. A
        /// critter in a trap has an age, a calorie count and a fertility. Rebuilt from a
        /// prefab they come back as different objects with all of that reset - and the
        /// original was deleted to make room for them.
        ///
        /// Assignable covers suits and anything else a duplicant is assigned to;
        /// GameTags.Creature covers live animals.
        /// </summary>
        private static bool IsEntityNotContents(GameObject go)
        {
            if (go == null) return false;
            return go.GetComponent<Assignable>() != null || go.HasTag(GameTags.Creature);
        }

        private static void ClearStorage(Storage storage)
        {
            for (int i = storage.items.Count - 1; i >= 0; i--)
            {
                var item = storage.items[i];

                // A container can hold an entry whose GameObject is already gone: the host
                // destroyed it and the removal has not reached us yet. DeleteObject() calls
                // GetComponent() on it and throws, which aborts the whole SyncVar dispatch —
                // so the container is never rebuilt and stays desynced until the next hard
                // sync. The stale entry is dropped by the RemoveAll below either way.
                if (item == null || item.IsNullOrDestroyed()) continue;

                // Left where it is. Skipping these on the sending side is not enough on
                // its own - the receiver empties the container before rebuilding it, so
                // without this the suit or the animal is deleted here and simply never
                // comes back.
                if (IsEntityNotContents(item)) continue;

                item.DeleteObject();
            }
            storage.items.RemoveAll(item => item == null || item.IsNullOrDestroyed());
        }
        
        // UP = Utility Path
        private const int UP_FIRST_CELL_BITS = 22;
        private const int UP_SEG_BITS = 4;
        private const int UP_SEG_COUNT_BITS = 2;
        private const int UP_MAX_SEGMENTS = 2;
        private const int UP_MAX_LEN_PER_SEG = 4;
        private const int UP_MAX_CELLS_PER_CHUNK = 1 + UP_MAX_SEGMENTS * UP_MAX_LEN_PER_SEG; // 9
        
        // Derived bit masks/shifts
        private const int UP_FIRST_CELL_MASK = (1 << UP_FIRST_CELL_BITS) - 1;
        private const int UP_SEGMENTS_BITS = UP_SEG_BITS * UP_MAX_SEGMENTS;
        private const int UP_SEGMENTS_MASK = (1 << UP_SEGMENTS_BITS) - 1;
        private const int UP_SEGMENTS_SHIFT = UP_FIRST_CELL_BITS;
        private const int UP_SEG_COUNT_MASK = (1 << UP_SEG_COUNT_BITS) - 1;
        private const int UP_SEG_COUNT_SHIFT = UP_FIRST_CELL_BITS + UP_SEGMENTS_BITS;
        
        /// <summary>
        /// Encodes a utility build path into an array of 32-bit chunks, each packing up to 9 cells.
        /// Bits 0-21: firstCell index. Bits 22-29: up to 2 direction-run segments (4-bit each:
        /// 2-bit direction + 2-bit run length-1). Bits 30-31: segment count.
        /// </summary>
        public static uint[] EncodeUtilityPath(List<BaseUtilityBuildTool.PathNode> path)
        {
            if (path == null || path.Count <= 1)
                return null;

            List<uint> chunks = new List<uint>();
            int pos = 0;
            int count = path.Count;

            while (pos < count)
            {
                int chunkEnd = pos + UP_MAX_CELLS_PER_CHUNK;
                if (chunkEnd > count)
                    chunkEnd = count;

                int chunkSize = chunkEnd - pos;
                if (chunkSize <= 1)
                    break;

                int firstCell = path[pos].cell;
                uint data = (uint)(firstCell & UP_FIRST_CELL_MASK);

                int segmentsPacked = 0;
                int segmentCount = 0;
                int i = pos + 1;

                while (i < chunkEnd && segmentCount < UP_MAX_SEGMENTS)
                {
                    int from = path[i - 1].cell;
                    int to = path[i].cell;
                    UtilityConnections dir = UtilityConnectionsExtensions.DirectionFromToCell(from, to);
                    if (dir == (UtilityConnections)0)
                        break;

                    int dirIndex;
                    if (dir == UtilityConnections.Right) dirIndex = 0;
                    else if (dir == UtilityConnections.Up) dirIndex = 1;
                    else if (dir == UtilityConnections.Left) dirIndex = 2;
                    else dirIndex = 3;

                    int len = 1;
                    i++;
                    while (i < chunkEnd && len < UP_MAX_LEN_PER_SEG)
                    {
                        int prev = path[i - 1].cell;
                        int curr = path[i].cell;
                        if (UtilityConnectionsExtensions.DirectionFromToCell(prev, curr) != dir)
                            break;
                        len++;
                        i++;
                    }

                    int seg = (dirIndex & UP_SEG_COUNT_MASK) | (((len - 1) & UP_SEG_COUNT_MASK) << UP_SEG_COUNT_BITS);
                    segmentsPacked |= seg << (segmentCount * UP_MAX_LEN_PER_SEG);
                    segmentCount++;
                }

                data |= (uint)(segmentsPacked & UP_SEGMENTS_MASK) << UP_SEGMENTS_SHIFT;
                data |= (uint)(segmentCount & UP_SEG_COUNT_MASK) << UP_SEG_COUNT_SHIFT;

                chunks.Add(data);
                pos = i;
            }

            return chunks.ToArray();
        }
        
        /// <summary>
        /// Decodes an array of 9-cell chunk uints back into a flat int[] of Grid cell indices.
        /// Each chunk is decoded via DecodeChunk and concatenated in order.
        /// </summary>
        public static int[] DecodeUtilityPath(uint[] pathData)
        {
            if (pathData == null || pathData.Length == 0)
                return null;

            List<int> cells = new List<int>(pathData.Length * UP_MAX_CELLS_PER_CHUNK);

            foreach (uint chunk in pathData)
            {
                if (chunk == 0)
                    continue;

                int[] chunkCells = DecodeUtilityPathChunk(chunk);
                if (chunkCells != null)
                    cells.AddRange(chunkCells);
            }

            return cells.ToArray();
        }

        /// <summary>
        /// Encodes a utility build path into an array of 64-bit chunks. Lower 32 bits = path data
        /// (same as EncodeUtilityPath). Upper 32 bits = validity bitmask (bits 0-8 for up to 9 cells).
        /// </summary>
        public static ulong[] EncodeUtilityPathWithValidity(List<BaseUtilityBuildTool.PathNode> path)
        {
            if (path == null || path.Count <= 1)
                return null;

            List<ulong> chunks = new List<ulong>();
            int pos = 0;
            int count = path.Count;

            while (pos < count)
            {
                int chunkEnd = pos + UP_MAX_CELLS_PER_CHUNK;
                if (chunkEnd > count)
                    chunkEnd = count;

                uint validityMask = 0;
                for (int j = pos; j < chunkEnd; j++)
                {
                    if (path[j].valid)
                        validityMask |= 1u << (j - pos);
                }

                int firstCell = path[pos].cell;
                uint data = (uint)(firstCell & UP_FIRST_CELL_MASK);

                int segmentsPacked = 0;
                int segmentCount = 0;
                int i = pos + 1;

                while (i < chunkEnd && segmentCount < UP_MAX_SEGMENTS)
                {
                    int from = path[i - 1].cell;
                    int to = path[i].cell;
                    UtilityConnections dir = UtilityConnectionsExtensions.DirectionFromToCell(from, to);
                    if (dir == (UtilityConnections)0)
                        break;

                    int dirIndex;
                    if (dir == UtilityConnections.Right) dirIndex = 0;
                    else if (dir == UtilityConnections.Up) dirIndex = 1;
                    else if (dir == UtilityConnections.Left) dirIndex = 2;
                    else dirIndex = 3;

                    int len = 1;
                    i++;
                    while (i < chunkEnd && len < UP_MAX_LEN_PER_SEG)
                    {
                        int prev = path[i - 1].cell;
                        int curr = path[i].cell;
                        if (UtilityConnectionsExtensions.DirectionFromToCell(prev, curr) != dir)
                            break;
                        len++;
                        i++;
                    }

                    int seg = (dirIndex & UP_SEG_COUNT_MASK) | (((len - 1) & UP_SEG_COUNT_MASK) << UP_SEG_COUNT_BITS);
                    segmentsPacked |= seg << (segmentCount * UP_MAX_LEN_PER_SEG);
                    segmentCount++;
                }

                data |= (uint)(segmentsPacked & UP_SEGMENTS_MASK) << UP_SEGMENTS_SHIFT;
                data |= (uint)(segmentCount & UP_SEG_COUNT_MASK) << UP_SEG_COUNT_SHIFT;

                chunks.Add(((ulong)validityMask << 32) | data);
                pos = i;
            }

            return chunks.ToArray();
        }

        /// <summary>
        /// Decodes a single 32-bit chunk into an array of Grid cell indices.
        /// Bits 0–21: firstCell. Bits 22–29: up to two 4-bit direction-run segments
        /// (2-bit direction, 2-bit run length − 1). Bits 30–31: segment count.
        /// Reconstructs cells by walking from firstCell through each direction-run.
        /// Returns null if data is 0 or firstCell is invalid.
        /// </summary>
        public static int[] DecodeUtilityPathChunk(uint data)
        {
            if (data == 0)
                return null;

            int firstCell = (int)(data & ((1 << UP_FIRST_CELL_BITS) - 1));
            int segmentsPacked = (int)((data >> UP_FIRST_CELL_BITS) & ((1 << (UP_SEG_BITS * UP_MAX_SEGMENTS)) - 1));
            int segmentCount = (int)((data >> (UP_FIRST_CELL_BITS + UP_SEG_BITS * UP_MAX_SEGMENTS)) & ((1 << UP_SEG_COUNT_BITS) - 1));

            if (!Grid.IsValidCell(firstCell))
                return null;

            List<int> cells = new List<int>(UP_MAX_CELLS_PER_CHUNK);
            cells.Add(firstCell);
            int cell = firstCell;

            for (int s = 0; s < segmentCount && s < UP_MAX_SEGMENTS; s++)
            {
                int seg = (segmentsPacked >> (s * UP_SEG_BITS)) & 0xF;
                int dir = seg & 0x3;
                int len = ((seg >> 2) & 0x3) + 1;

                int delta;
                switch (dir)
                {
                    case 0: delta = 1; break;
                    case 1: delta = Grid.WidthInCells; break;
                    case 2: delta = -1; break;
                    case 3: delta = -Grid.WidthInCells; break;
                    default: continue;
                }

                for (int i = 0; i < len; i++)
                {
                    cell += delta;
                    if (!Grid.IsValidCell(cell))
                        break;
                    cells.Add(cell);
                }
            }

            return cells.ToArray();
        }

        [HarmonyPatch(typeof(LogicPorts), nameof(LogicPorts.OnSpawn))]
		public class LogicPorts_OnSpawn_Patch
		{
			public static void Postfix(LogicPorts __instance) => LogicPortsCmps.Add(__instance);
		}
		[HarmonyPatch(typeof(LogicPorts), nameof(LogicPorts.OnCleanUp))]
		public class LogicPorts_OnCleanUp_Patch
		{
			public static void Prefix(LogicPorts __instance) => LogicPortsCmps.Remove(__instance);
		}

		static readonly global::Components.Cmps<LogicPorts> LogicPortsCmps = new();
		static readonly HashSet<ILogicUIElement> AllLogicPortCells = [];
		/// <summary>
		/// Iterates every ILogicUIElement in uiVisElements and removes any whose
		/// cell has no Building component on any object layer. Called periodically
		/// to sweep up orphaned port entries that survive the normal cleanup path.
		/// </summary>
		public static void CleanupOrphanedLogicVisElements()
		{
			var mgr = Game.Instance.logicCircuitManager;
			foreach (var portVis in mgr.GetVisElements())
			{
				AllLogicPortCells.Add(portVis);
			}
			foreach (LogicPorts portsComponent in LogicPortsCmps)
			{
				if (portsComponent.IsNullOrDestroyed())
					continue;

				foreach (var inPort in portsComponent.inputPorts)
					AllLogicPortCells.Remove(inPort);

				foreach (var inPort in portsComponent.outputPorts)
					AllLogicPortCells.Remove(inPort);
			}
			foreach (var orphan in AllLogicPortCells)
				mgr.RemoveVisElem(orphan);
		}

    }
}
