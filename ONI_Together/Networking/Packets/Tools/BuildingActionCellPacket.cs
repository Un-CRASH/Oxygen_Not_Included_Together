using System.Collections.Generic;
using System.IO;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Packets.Tools
{
	// Covers the non-drag deconstruct/cancel entry points. The drag path already
	// synchronizes through DeconstructPacket / CancelPacket.
	public enum BuildingActionKind : byte
	{
		QueueDeconstruct = 1,
		CancelDeconstruct = 2,
		CancelConstruct = 3,
	}

	// The new wire type also changes the registry fingerprint: packet ids hash
	// type names, so extending the old BuildingActionPacket would not reject old clients.
	public class BuildingActionCellPacket : IPacket
	{
		public static bool ProcessingIncoming;

		private ulong Sender;
		public int NetId;
		public BuildingActionKind Action;
		public int Cell = Grid.InvalidCell;
		public int Layer = -1;
		public string PrefabID = string.Empty;

		public BuildingActionCellPacket() { }

		public BuildingActionCellPacket(NetworkIdentity identity, BuildingActionKind action)
		{
			Sender = NetworkConfig.GetLocalID();
			NetId = identity.NetId;
			Action = action;
			var go = identity.gameObject;
			var prefab = go.GetComponent<KPrefabID>();
			PrefabID = prefab != null ? prefab.PrefabTag.ToString() : string.Empty;
			Cell = Grid.PosToCell(go);
			if (TryCaptureCell(go, Cell)) return;

			var building = go.GetComponent<Building>();
			if (building == null || building.Def == null) return;
			// A bridge's pivot can be empty in Grid.Objects; send an occupied footprint cell.
			foreach (int placementCell in building.PlacementCells)
				if (TryCaptureCell(go, placementCell)) return;

			// Cancellation can already have cleared the grid in another event subscriber.
			var constructable = go.GetComponent<Constructable>();
			Layer = (int)(constructable != null && constructable.IsReplacementTile
				? building.Def.ReplacementLayer : building.Def.ObjectLayer);
		}

		private bool TryCaptureCell(GameObject go, int cell)
		{
			if (!Grid.IsValidCell(cell)) return false;
			for (int layer = 0; layer < (int)ObjectLayer.NumLayers; layer++)
			{
				if (Grid.Objects[cell, layer] != go) continue;
				Cell = cell;
				Layer = layer;
				return true;
			}
			return false;
		}

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();
			writer.Write(Sender);
			writer.Write(NetId);
			writer.Write((byte)Action);
			writer.Write(Cell);
			writer.Write(Layer);
			writer.Write(PrefabID);
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();
			Sender = reader.ReadUInt64();
			NetId = reader.ReadInt32();
			Action = (BuildingActionKind)reader.ReadByte();
			Cell = reader.ReadInt32();
			Layer = reader.ReadInt32();
			PrefabID = reader.ReadString();
		}

		private bool MatchesTarget(GameObject go)
		{
			if (go == null || !go.TryGetComponent<KPrefabID>(out var prefab) ||
				prefab.PrefabTag.ToString() != PrefabID) return false;
			if (Layer >= 0)
			{
				if (Grid.Objects[Cell, Layer] != go) return false;
			}
			else if (Grid.PosToCell(go) != Cell) return false;

			// A completed building must not receive a delayed construction cancellation.
			return Action == BuildingActionKind.CancelConstruct
				? go.GetComponent<Constructable>() != null
				: go.GetComponent<Deconstructable>() != null;
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();
			if (Sender == NetworkConfig.GetLocalID()) return;
			if (!Grid.IsValidCell(Cell) || Layer < -1 || Layer >= (int)ObjectLayer.NumLayers ||
				string.IsNullOrEmpty(PrefabID) ||
				(Action != BuildingActionKind.QueueDeconstruct &&
				 Action != BuildingActionKind.CancelDeconstruct && Action != BuildingActionKind.CancelConstruct)) return;

			GameObject target = null;
			try
			{
				if (NetworkIdentityRegistry.TryGet(NetId, out var identity) && identity != null &&
					MatchesTarget(identity.gameObject)) target = identity.gameObject;
				if (target == null && Layer >= 0)
				{
					var candidate = Grid.Objects[Cell, Layer];
					if (MatchesTarget(candidate)) target = candidate;
				}

				if (target != null)
				{
					bool wasProcessingIncoming = ProcessingIncoming;
					ProcessingIncoming = true;
					using var scope = OrderApplyScope.Enter();
					try
					{
						if (DebugConsole.IsVerbose) DebugConsole.LogVerbose($"[BuildingAction] apply NetId={NetId} kind={Action} name={target.name} cell={Cell}");
						switch (Action)
						{
							case BuildingActionKind.QueueDeconstruct:
								target.GetComponent<Deconstructable>().QueueDeconstruction(userTriggered: true);
								break;
							case BuildingActionKind.CancelDeconstruct:
								target.GetComponent<Deconstructable>().CancelDeconstruction();
								break;
							case BuildingActionKind.CancelConstruct:
								// Constructable clears material needs; Cancellable deletes the plan.
								target.Trigger(2127324410);
								break;
						}
					}
					finally
					{
						ProcessingIncoming = wasProcessingIncoming;
					}
				}
				else if (Action == BuildingActionKind.QueueDeconstruct)
				{
					DebugConsole.LogAggregated("BuildingAction.NotFound", $"[BuildingActionCellPacket] Target not found (NetId={NetId}, Cell={Cell}, Prefab={PrefabID}, action={Action})");
				}
				else if (DebugConsole.IsVerbose)
				{
					DebugConsole.LogVerbose($"[BuildingActionCellPacket] Cancel target already gone (NetId={NetId}, Cell={Cell}, Prefab={PrefabID}, action={Action})");
				}
			}
			catch (System.Exception ex)
			{
				DebugConsole.LogError($"[BuildingActionCellPacket] Exception handling {Action} on NetId {NetId}: {ex}");
				return;
			}

			// The other client may still have the plan after the host completed it.
			// Relay cancellations even when the host's matching target is already gone.
			if (MultiplayerSession.IsHost && (target != null || Action != BuildingActionKind.QueueDeconstruct))
				PacketSender.SendToAllExcluding(this, new HashSet<ulong> { MultiplayerSession.HostUserID, Sender });
		}
	}
}
