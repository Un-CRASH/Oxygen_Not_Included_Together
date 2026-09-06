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
	/// </summary>
	public class ClearableActionPacket : IPacket
	{
		public static bool ProcessingIncoming;

		public int NetId;
		public int Cell;
		public bool IsMarked;

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(NetId);
			writer.Write(Cell);
			writer.Write(IsMarked);
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			NetId = reader.ReadInt32();
			Cell = reader.ReadInt32();
			IsMarked = reader.ReadBoolean();
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			Clearable target = null;
			try
			{
				ProcessingIncoming = true;

				if (NetId != 0 && NetworkIdentityRegistry.TryGet(NetId, out var identity) && identity != null)
				{
					target = identity.GetComponent<Clearable>();
				}

				if (target == null && Grid.IsValidCell(Cell))
				{
					var pickupableGo = Grid.Objects[Cell, (int)ObjectLayer.Pickupables];
					if (pickupableGo != null)
					{
						target = pickupableGo.GetComponent<Clearable>();
					}
				}

				if (target != null)
				{
					if (IsMarked)
					{
						target.MarkForClear();
					}
					else
					{
						target.CancelClearing();
					}
				}
				else
				{
					DebugConsole.LogAggregated("ClearableAction.NotFound", $"[ClearableActionPacket] Target not found (NetId={NetId}, Cell={Cell})");
				}
			}
			catch (System.Exception ex)
			{
				DebugConsole.LogError($"[ClearableActionPacket] Exception during dispatch: {ex}");
			}
			finally
			{
				ProcessingIncoming = false;
			}

			// Host relays client actions to other clients. An item the host cannot find by
			// id or by cell is local to the sender; the other clients cannot have it either.
			if (MultiplayerSession.IsHost && target != null)
			{
				PacketSender.SendToAllClients(this);
			}
		}
	}
}
