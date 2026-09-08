using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Tools;
using System.Collections.Generic;
using System.IO;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Packets.World
{
	public class PrioritizeStatePacket : IPacket
	{
		public struct PriorityData
		{
			public int NetId;
			public int Cell;
			/// <summary>Grid.Objects layer the object occupies at Cell, or -1 for a pickupable / unknown.</summary>
			public int Layer;
			public int PriorityClass;
			public int PriorityValue;
		}

		public List<PriorityData> Priorities = new List<PriorityData>();
		public ulong Sender;
		public static bool IsApplying = false;

		public static int LayerOf(GameObject go, int cell)
		{
			if (go == null || !Grid.IsValidCell(cell)) return -1;
			for (int layer = 0; layer < (int)ObjectLayer.NumLayers; layer++)
				if (layer != (int)ObjectLayer.Pickupables && Grid.Objects[cell, layer] == go)
					return layer;
			return -1;
		}

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(Sender);
			writer.Write(Priorities.Count);
			foreach (var p in Priorities)
			{
				writer.Write(p.NetId);
				writer.Write(p.Cell);
				writer.Write(p.Layer);
				writer.Write(p.PriorityClass);
				writer.Write(p.PriorityValue);
			}
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			Sender = reader.ReadUInt64();
			int count = reader.ReadInt32();
			if (count < 0 || count > 4096)
			{
				Priorities = new List<PriorityData>();
				return;
			}
			Priorities = new List<PriorityData>(count);
			for (int i = 0; i < count; i++)
			{
				Priorities.Add(new PriorityData
				{
					NetId = reader.ReadInt32(),
					Cell = reader.ReadInt32(),
					Layer = reader.ReadInt32(),
					PriorityClass = reader.ReadInt32(),
					PriorityValue = reader.ReadInt32()
				});
			}
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (Sender == NetworkConfig.GetLocalID())
				return;

			// Both host and client need to apply priority changes
			bool wasApplying = IsApplying;
			using var scope = OrderApplyScope.Enter();
			try
			{
				IsApplying = true;
				foreach (var p in Priorities)
				{
					Prioritizable prioritizable = null;
					if (NetworkIdentityRegistry.TryGet(p.NetId, out var identity) && identity != null)
					{
						prioritizable = identity.GetComponent<Prioritizable>();
					}

					// Fallback by cell. The sender says which layer its object sits on; a
					// building's priority must not land on whatever debris lies at its cell.
					if (prioritizable == null && Grid.IsValidCell(p.Cell))
					{
						GameObject candidate = null;
						if (p.Layer >= 0 && p.Layer < (int)ObjectLayer.NumLayers)
							candidate = Grid.Objects[p.Cell, p.Layer];
						else if (p.Layer == -1)
							candidate = Grid.Objects[p.Cell, (int)ObjectLayer.Pickupables];
						if (candidate != null)
							prioritizable = candidate.GetComponent<Prioritizable>();
					}

					if (prioritizable == null)
						DebugConsole.LogAggregated("PrioritizeState.NotFound", $"[PrioritizeStatePacket] Target not found (NetId={p.NetId}, Cell={p.Cell}, Layer={p.Layer})");

					if (prioritizable != null)
					{
						var newSetting = new PrioritySetting((PriorityScreen.PriorityClass)p.PriorityClass, p.PriorityValue);
						// Only update if different to avoid event spam
						if (!prioritizable.GetMasterPriority().Equals(newSetting))
						{
							prioritizable.SetMasterPriority(newSetting);
						}
					}
				}
			}
			finally
			{
				IsApplying = wasApplying;
			}

			// The host relays a client's change to the other clients, never back to the
			// client that made it.
			if (MultiplayerSession.IsHost && Priorities.Count > 0)
			{
				PacketSender.SendToAllExcluding(this, [MultiplayerSession.HostUserID, Sender]);
			}
		}
	}
}
