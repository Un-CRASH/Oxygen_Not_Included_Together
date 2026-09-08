using System.Collections.Generic;

namespace ONI_Together.Networking.Packets.Architecture
{
	/// <summary>
	/// Keeps a variable-length unreliable packet inside one datagram. Anything larger
	/// than the transport's single-datagram limit is fragmented, and a fragmented payload
	/// can only be delivered reliably; for periodic snapshots the tail entries are worth
	/// less than a packet that arrives.
	/// </summary>
	internal static class PacketSizeGuard
	{
		/// <summary>Serialized size (with the packet id) an unreliable packet may reach.</summary>
		public const int UnreliableBudgetBytes = 960;

		public static void TrimToBudget<T>(IPacket packet, List<T> entries, int budget = UnreliableBudgetBytes)
		{
			if (entries == null || entries.Count <= 1)
				return;
			int size = PacketSender.SerializePacketForSending(packet).Length;
			if (size <= budget)
				return;
			// Entries are roughly equal in size: cut proportionally, then settle by measuring.
			int keep = System.Math.Max(1, (int)(entries.Count * (budget / (float)size)));
			entries.RemoveRange(keep, entries.Count - keep);
			while (entries.Count > 1 && PacketSender.SerializePacketForSending(packet).Length > budget)
				entries.RemoveAt(entries.Count - 1);
		}
	}
}
