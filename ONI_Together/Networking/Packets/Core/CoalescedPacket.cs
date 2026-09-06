using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using System;
using System.Collections.Generic;
using System.IO;

namespace ONI_Together.Networking.Packets.Core
{
	/// <summary>
	/// One reliable wire packet carrying several serialized packets, in send order.
	///
	/// LiteNetLib's reliable-ordered channel keeps at most 64 packets in flight per
	/// round trip whatever their size, and the world stream is hundreds of packets a
	/// second, most of them a few dozen bytes. Over the VPN the channel fell minutes
	/// behind: a building the host placed reached a client 5 to 9 minutes later, or
	/// never when a hard sync dropped the connection first. TransportPacketSender packs
	/// a frame's reliable packets into one of these, so the limit becomes a byte limit
	/// the link is nowhere near.
	///
	/// Each entry is exactly what PacketSender.SerializePacketForSending produces
	/// (packet id + payload), so the receiver hands it to PacketHandler.HandleIncoming
	/// unchanged: the no-world gate, the tracker and the per-packet exception handling
	/// apply to every entry as if it had arrived on its own.
	/// </summary>
	internal class CoalescedPacket : IPacket
	{
		// The sender keeps a container under 1000 bytes, so an entry can never exceed that
		// and fewer than 128 fit; the reader refuses anything claiming more.
		public const int MaxEntries = 128;
		private const int MaxEntryBytes = 1000;

		public List<byte[]> Entries = new List<byte[]>();

		public void Serialize(BinaryWriter writer)
		{
			writer.Write(Entries.Count);
			foreach (var entry in Entries)
			{
				writer.Write(entry.Length);
				writer.Write(entry);
			}
		}

		public void Deserialize(BinaryReader reader)
		{
			int count = reader.ReadInt32();
			if (count < 0 || count > MaxEntries)
			{
				DebugConsole.LogWarning($"[CoalescedPacket] Invalid entry count: {count}");
				Entries = new List<byte[]>();
				return;
			}

			Entries = new List<byte[]>(count);
			for (int i = 0; i < count; i++)
			{
				int length = reader.ReadInt32();
				if (length < 0 || length > MaxEntryBytes)
				{
					DebugConsole.LogWarning($"[CoalescedPacket] Invalid entry length: {length}");
					Entries.Clear();
					return;
				}
				Entries.Add(reader.ReadBytes(length));
			}
		}

		public void OnDispatched()
		{
			foreach (var entry in Entries)
			{
				try
				{
					PacketHandler.HandleIncoming(entry);
				}
				catch (Exception ex)
				{
					DebugConsole.LogError("[CoalescedPacket] Exception while handling an inner packet: " + ex);
				}
			}
		}
	}
}
