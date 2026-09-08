using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Profiling;
using System.IO;
using UnityEngine;

namespace ONI_Together.Networking.Packets.Tools.Clear
{
	/// <summary>
	/// Synchronizes individual ground item sweep/clear commands when a player clicks an item
	/// and clicks "Sweep" or "Cancel Sweep" on the side screen, bypassing the area drag tool.
	///
	/// Pickupable ids are not reproducible across peers (they hash the pile's mass and
	/// temperature), so the id rarely resolves on the other side. The fallback is the
	/// item at Cell with the same prefab: position alone is shared by every pile lying
	/// at a cell, and a sweep for the sender's ore used to land on the receiver's seed.
	/// </summary>
	public class ClearableActionPacket : IPacket
	{
		public static bool ProcessingIncoming;

		public int NetId;
		public int Cell;
		public bool IsMarked;
		public string PrefabID = string.Empty;

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(NetId);
			writer.Write(Cell);
			writer.Write(IsMarked);
			writer.Write(PrefabID ?? string.Empty);
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			NetId = reader.ReadInt32();
			Cell = reader.ReadInt32();
			IsMarked = reader.ReadBoolean();
			PrefabID = reader.ReadString();
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			Clearable target = null;
			bool wasProcessing = ProcessingIncoming;
			try
			{
				ProcessingIncoming = true;
				using var scope = OrderApplyScope.Enter();

				if (NetId != 0 && NetworkIdentityRegistry.TryGet(NetId, out var identity, logFailure: false) && identity != null)
				{
					target = identity.GetComponent<Clearable>();
					if (target != null && !string.IsNullOrEmpty(PrefabID) && !MatchesPrefab(target.gameObject))
						target = null;
				}

				if (target == null && Grid.IsValidCell(Cell) && !string.IsNullOrEmpty(PrefabID))
					target = FindAtCell();

				if (target != null)
				{
					if (IsMarked)
						target.MarkForClear();
					else
						target.CancelClearing();
				}
				else
				{
					DebugConsole.LogAggregated("ClearableAction.NotFound", $"[ClearableActionPacket] Target not found (NetId={NetId}, Cell={Cell}, Prefab={PrefabID})");
				}
			}
			catch (System.Exception ex)
			{
				DebugConsole.LogError($"[ClearableActionPacket] Exception during dispatch: {ex}");
			}
			finally
			{
				ProcessingIncoming = wasProcessing;
			}

			// Host relays client actions to other clients. An item the host cannot find by
			// id or by cell is local to the sender; the other clients cannot have it either.
			if (MultiplayerSession.IsHost && target != null)
			{
				PacketSender.SendToAllClients(this);
			}
		}

		private bool MatchesPrefab(GameObject go)
		{
			return go != null && go.TryGetComponent<KPrefabID>(out var prefab) && prefab.PrefabTag.ToString() == PrefabID;
		}

		/// <summary>The first item of the sender's prefab at the cell whose mark differs from the order (the others are already as ordered).</summary>
		private Clearable FindAtCell()
		{
			var head = Grid.Objects[Cell, (int)ObjectLayer.Pickupables];
			var item = head != null ? head.GetComponent<Pickupable>()?.objectLayerListItem : null;
			Clearable sameState = null;
			int guard = 0;
			while (item != null && guard++ < 10000)
			{
				var go = item.gameObject;
				if (go != null && MatchesPrefab(go) && go.TryGetComponent<Clearable>(out var clearable))
				{
					if (clearable.isMarkedForClear != IsMarked)
						return clearable;
					sameState ??= clearable;
				}
				item = item.nextItem;
			}
			return sameState;
		}
	}
}
