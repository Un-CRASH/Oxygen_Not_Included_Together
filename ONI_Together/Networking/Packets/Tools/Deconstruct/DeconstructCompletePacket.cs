using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using System.IO;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Packets.Tools.Deconstruct
{
	/// <summary>
	/// The host finished deconstructing a building; the clients remove their copy.
	///
	/// The copy is looked up by cell and layer first, then, because the client's object
	/// can sit on another layer (a bridge, a replacement tile, a building the client
	/// raised differently), on every layer of the cell by prefab. Before that fallback
	/// 143 of 145 removals in one session found nothing and said nothing.
	/// </summary>
	public class DeconstructCompletePacket : IPacket
	{
		public int Cell, ObjectLayer;
		public string PrefabID = string.Empty;

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(Cell);
			writer.Write(ObjectLayer);
			writer.Write(PrefabID ?? string.Empty);
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			Cell = reader.ReadInt32();
			ObjectLayer = reader.ReadInt32();
			PrefabID = reader.ReadString();
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (MultiplayerSession.IsHost)
				return;

			if (!Grid.IsValidCell(Cell))
			{
				DebugConsole.LogWarning($"[DeconstructCompletePacket] Invalid cell {Cell}");
				return;
			}

			using var scope = OrderApplyScope.Enter();

			bool layerValid = ObjectLayer >= 0 && ObjectLayer < (int)global::ObjectLayer.NumLayers;
			GameObject onLayer = layerValid ? Grid.Objects[Cell, ObjectLayer] : null;
			GameObject go = onLayer;
			if (go != null && !string.IsNullOrEmpty(PrefabID) && !MatchesPrefab(go))
				go = null;

			if (go == null)
				go = FindByPrefab();

			if (go == null)
			{
				DebugConsole.LogAggregated("DeconstructComplete.NotFound", $"[DeconstructCompletePacket] Nothing to remove at cell {Cell} layer {ObjectLayer} ({PrefabID}); layer holds {Describe(onLayer)}");
				return;
			}

			if (go.TryGetComponent<Deconstructable>(out var deconstructable) && !deconstructable.HasBeenDestroyed)
			{
				DebugConsole.Log($"[DeconstructCompletePacket] Removing {go.name} at cell {Cell} on objectlayer {ObjectLayer} on client.");
				// Material drops are host authoritative. Running the full deconstruction
				// path again can access storage after cleanup (the coat rack crash).
				Util.KDestroyGameObject(go);
			}
			else
			{
				DebugConsole.LogAggregated("DeconstructComplete.NotDeconstructable", $"[DeconstructCompletePacket] {go.name} at cell {Cell} has no live Deconstructable; not removed");
			}
		}

		private bool MatchesPrefab(GameObject go)
		{
			return go.TryGetComponent<KPrefabID>(out var prefab) && prefab.PrefabTag.ToString() == PrefabID;
		}

		private GameObject FindByPrefab()
		{
			if (string.IsNullOrEmpty(PrefabID))
				return null;
			for (int layer = 0; layer < (int)global::ObjectLayer.NumLayers; layer++)
			{
				if (layer == (int)global::ObjectLayer.Pickupables) continue;
				var candidate = Grid.Objects[Cell, layer];
				if (candidate != null && MatchesPrefab(candidate) && candidate.GetComponent<Deconstructable>() != null)
					return candidate;
			}
			return null;
		}

		private static string Describe(GameObject go)
		{
			return go == null ? "nothing" : go.name;
		}
	}
}
