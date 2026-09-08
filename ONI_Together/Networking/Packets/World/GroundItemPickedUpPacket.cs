using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using System.Collections.Generic;
using System.IO;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.World
{
	/// <summary>
	/// Host -> clients: remove a tracked ground item by NetId.
	/// 4 bytes per item. WorldDamageSpawnResourcePacket already assigns matching NetIds
	/// via identity.OverrideNetId(NetId), so client registry lookup is reliable.
	/// Keep this packet immediate so the PR does not depend on the separate
	/// bulk-flush fix branch to dispatch small pickup bursts.
	/// </summary>
	public class GroundItemPickedUpPacket : IPacket
	{
		public int NetId;

		public static bool TryConsumePending(int netId)
		{
			using var _ = Profiler.Scope();
			return Networking.Components.PendingRemovals.TryConsume(netId);
		}

		public static void ClearPending()
		{
			using var _ = Profiler.Scope();
			Networking.Components.PendingRemovals.Clear();
		}

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();
			writer.Write(NetId);
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();
			NetId = reader.ReadInt32();
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (!NetworkIdentityRegistry.TryGetComponent<Pickupable>(NetId, out var pickupable))
			{
				Networking.Components.PendingRemovals.Add(NetId);
				DebugConsole.LogAggregated("PendingPickup.Ground", $"[GroundItemPickedUpPacket] Pickupable NetId {NetId} not yet registered; queued pending removal");
				return;
			}

			Util.KDestroyGameObject(pickupable.gameObject);
		}
	}
}
