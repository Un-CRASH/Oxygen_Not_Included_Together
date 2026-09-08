using Newtonsoft.Json;
using ONI_Together.DebugTools;
using ONI_Together.Misc;
using ONI_Together.Networking.Packets.Architecture;
using Steamworks;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shared.Profiling;
using UnityEngine;
using static TUNING.BUILDINGS.UPGRADES;

namespace ONI_Together.Networking.Packets.Tools.Build
{
	public class UtilityBuildPacket : IPacket
	{
		private const int MaxPathNodeCount = 8192;
		private const int MaxMaterialTagCount = 64;

		/// <summary>
		/// Gets a value indicating whether incoming messages are currently being processed.
		/// Use in patches to prevent recursion when applying tool changes.
		/// </summary>
		public static bool ProcessingIncoming { get; private set; } = false;

		internal static void ResetState() => ProcessingIncoming = false;

		public ulong[] PathChunks;
		public List<string> MaterialTags = [];
		public string PrefabID, FacadeID;
		public PrioritySetting Priority;
		public bool InstantBuild;

		public UtilityBuildPacket() { }

		public UtilityBuildPacket(string prefabId, List<BaseUtilityBuildTool.PathNode> nodes, List<string> mats, string skin, bool instantBuild = false)
		{
			using var _ = Profiler.Scope();

			PrefabID = prefabId ?? string.Empty;
			PathChunks = BuildingUtils.EncodeUtilityPathWithValidity(nodes ?? []);
			MaterialTags = mats ?? [];
			FacadeID = skin ?? string.Empty;
			InstantBuild = instantBuild;

			if (PlanScreen.Instance)
				Priority = PlanScreen.Instance.GetBuildingPriority();
		}
		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(PrefabID);
			writer.Write(FacadeID);
			writer.Write(PathChunks?.Length ?? 0);
			if (PathChunks != null)
			{
				for (int i = 0; i < PathChunks.Length; i++)
					writer.Write(PathChunks[i]);
			}
			writer.Write(MaterialTags.Count);
			foreach (var tag in MaterialTags)
				writer.Write(tag);
			writer.Write((int)Priority.priority_class);
			writer.Write(Priority.priority_value);
			writer.Write(InstantBuild);
		}


		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			PrefabID = reader.ReadString();
			FacadeID = reader.ReadString();

			int chunkCount = reader.ReadInt32();
			if (chunkCount < 0 || chunkCount > MaxPathNodeCount)
			{
				DebugConsole.LogWarning($"[UtilityBuildPacket] Invalid chunk count: {chunkCount}");
				PathChunks = null;
				MaterialTags = [];
				return;
			}
			PathChunks = new ulong[chunkCount];
			for (int i = 0; i < chunkCount; i++)
				PathChunks[i] = reader.ReadUInt64();

			int matCount = reader.ReadInt32();
			if (matCount < 0 || matCount > MaxMaterialTagCount)
			{
				DebugConsole.LogWarning($"[UtilityBuildPacket] Invalid material tag count: {matCount}");
				PathChunks = null;
				MaterialTags = [];
				return;
			}
			MaterialTags = new List<string>(matCount);
			for (int i = 0; i < matCount; i++)
				MaterialTags.Add(reader.ReadString());

			Priority = new PrioritySetting(
					(PriorityScreen.PriorityClass)reader.ReadInt32(),
					reader.ReadInt32());
			InstantBuild = reader.ReadBoolean();
		}

		public void OnDispatched()
		{
			using var scope = Profiler.Scope();

			DebugConsole.Log("[UtilityBuildPacket] OnDispatched");
			if (PathChunks == null || PathChunks.Length == 0)
			{
				DebugConsole.LogWarning("[UtilityBuildPacket] Received empty path, ignoring.");
				return;
			}

			List<BaseUtilityBuildTool.PathNode> path = new List<BaseUtilityBuildTool.PathNode>();
			for (int c = 0; c < PathChunks.Length; c++)
			{
				ulong chunk = PathChunks[c];
				int[] chunkCells = BuildingUtils.DecodeUtilityPathChunk((uint)(chunk & 0xFFFFFFFF)); /// Lower 32 bits of the ulong chunk contain the packed path data (firstCell + direction-run segments).
				if (chunkCells == null) continue;

				uint validityMask = (uint)(chunk >> 32);
				for (int j = 0; j < chunkCells.Length; j++)
				{
					path.Add(new BaseUtilityBuildTool.PathNode
					{
						cell = chunkCells[j],
						valid = (validityMask & (1u << j)) != 0
					});
				}
			}

			if (path.Count == 0)
			{
				DebugConsole.LogWarning("[UtilityBuildPacket] Decoded empty path, ignoring.");
				return;
			}

			var def = Assets.GetBuildingDef(PrefabID);
			if (def == null)
			{
				DebugConsole.LogError($"[UtilityBuildPacket] Unknown PrefabID: {PrefabID}");
				return;
			}

			var selected_elements = MaterialTags.Select(t => TagManager.Create(t)).ToList();
			if (selected_elements.Count == 0)
			{
				selected_elements.AddRange(def.DefaultElements());
			}
			///mirrored from BuildMenu OnRecipeElementsFullySelected
			BaseUtilityBuildTool tool = def.BuildingComplete.TryGetComponent<Wire>(out _) ? WireBuildTool.Instance : UtilityBuildTool.Instance;

			if(PlanScreen.Instance?.ProductInfoScreen?.materialSelectionPanel?.PriorityScreen == null)
			{
				PlanScreen.Instance.CopyBuildingOrder(def,FacadeID);
				PlanScreen.Instance.OnActiveToolChanged(SelectTool.Instance);
			}

			///caching existing stuff on the tool
			var cachedDef = tool.def;
			List<BaseUtilityBuildTool.PathNode> cachedPath = tool.path != null ? [.. tool.path] : [];
			IList<Tag> cachedMaterials = tool.selectedElements != null ? [.. tool.selectedElements] : [];
			var cachedMgr = tool.conduitMgr;

			IHaveUtilityNetworkMgr conduitManagerHaver = def.BuildingComplete.GetComponent<IHaveUtilityNetworkMgr>();

			tool.def = def;
			tool.path = path;
			tool.selectedElements = selected_elements;
			tool.conduitMgr = conduitManagerHaver.GetNetworkManager();

			ProcessingIncoming = true;
			bool cachedInstantBuildMode = DebugHandler.InstantBuildMode;
			DebugHandler.InstantBuildMode = InstantBuild;
			// Priorities set on the path nodes are part of this order, not new ones.
			using var applyScope = OrderApplyScope.Enter();
			try
			{
				tool.BuildPath();

				foreach (BaseUtilityBuildTool.PathNode node in path)
				{
                    GameObject gameObject = Grid.Objects[node.cell, (int)def.TileLayer];
                    if (gameObject == null)
                        continue;
                    if (gameObject.TryGetComponent<Prioritizable>(out var prioritizable))
                        prioritizable?.SetMasterPriority(Priority);
                    if (gameObject.TryGetComponent<KAnimGraphTileVisualizer>(out var vis))
                    {
                        vis.UpdateConnections(vis.Connections);
                        vis.Refresh();
                    }
                }
			}
			finally
			{
				DebugHandler.InstantBuildMode = cachedInstantBuildMode;
				ProcessingIncoming = false;
				tool.def = cachedDef;
				tool.path = cachedPath;
				tool.selectedElements = cachedMaterials;
				tool.conduitMgr = cachedMgr;
			}
		}
	}
}
