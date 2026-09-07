using Epic.OnlineServices.P2P;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.DuplicantActions;
using Steamworks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.Core
{
	internal class BulkSenderPacket : IPacket
	{
		public BulkSenderPacket() { }
		public BulkSenderPacket(int packetId, List<byte[]> innerData)
		{
			using var _ = Profiler.Scope();

			InnerPacketId = packetId;
			SerializedInnerPackets = innerData;
		}

		public int InnerPacketId;
		public List<byte[]> SerializedInnerPackets = [];

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(InnerPacketId);
			int packetCount = SerializedInnerPackets.Count;
			writer.Write(packetCount);
			for (int i = 0; i < packetCount; i++)
			{
				var serializedPacket = SerializedInnerPackets[i];
				writer.Write(serializedPacket.Length);
				writer.Write(serializedPacket);
			}
			//DebugConsole.LogSuccess("Dispatching bulk packet of type " + PacketRegistry.Create(InnerPacketId).GetType().Name + " with " + SerializedInnerPackets.Count() + " packets innit");

		}
		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			InnerPacketId = reader.ReadInt32();
			int packetCount = reader.ReadInt32();
			// Every entry needs at least its length field. Validate before allocating.
			if (packetCount < 0 || packetCount > (reader.BaseStream.Length - reader.BaseStream.Position) / sizeof(int))
				throw new InvalidDataException("Invalid bulk packet count: " + packetCount);
			SerializedInnerPackets = new List<byte[]>(packetCount);
			for (int i = 0; i < packetCount; i++)
			{
				int length = reader.ReadInt32();
				if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
					throw new InvalidDataException("Invalid bulk entry length: " + length);
				var packetData = reader.ReadBytes(length);
				SerializedInnerPackets.Add(packetData);
			}
		}
		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (!PacketRegistry.HasRegisteredPacket(InnerPacketId))
			{
				DebugConsole.LogWarning("[BulkSenderPacket] unknown inner packet id found, cannot unpack: " + InnerPacketId);
				return;
			}
			//DebugConsole.Log("[BulkSenderPacket] received with "+SerializedInnerPackets.Count()+" packets of type " + PacketRegistry.Create(InnerPacketId).GetType().Name + ", dispatching");

			foreach (var packetData in SerializedInnerPackets)
			{
				try
				{
					var innerPacket = PacketRegistry.Create(InnerPacketId);
					using var ms = new MemoryStream(packetData);
					using var reader = new BinaryReader(ms);
					innerPacket.Deserialize(reader);
					// Bulk entries still need the no-world gate applied individually.
					if (PacketHandler.ShouldDispatchWithoutWorld(innerPacket))
						innerPacket.OnDispatched();
				}
				catch (Exception ex)
				{
					// Entries are framed independently: one failure must not drop the rest.
					DebugConsole.LogAggregated("BulkSenderPacket.Inner." + InnerPacketId,
						$"[BulkSenderPacket] Failed inner packet {InnerPacketId}: {ex}");
				}
			}
		}
	}
}
