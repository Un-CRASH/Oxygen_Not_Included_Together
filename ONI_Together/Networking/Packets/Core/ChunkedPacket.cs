using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace ONI_Together.Networking.Packets.Core
{
	internal class ChunkedPacket : IPacket
	{
		public int SequenceId;
		public int ChunkIndex;
		public int TotalChunks;
		public byte[] ChunkData;

		// TransportPacketSender uses 980-byte fragments. 65536 fragments allow over
		// 61 MiB, including WorldDataPacket's 32 MiB compressed limit and its headers.
		private const int MaxChunkBytes = 980;
		private const int MaxChunksPerTransfer = 65536;
		private const int MaxPendingTransfers = 64;
		private const int MaxPendingBytes = 128 * 1024 * 1024;
		private const float IdleTimeout = 120f;

		private sealed class PendingTransfer
		{
			public byte[][] Chunks;
			public int ReceivedCount;
			public int ByteCount;
			public float LastReceived;
		}

		// NetPeer inherits IPEndPoint.Equals: equal addresses can be DIFFERENT
		// connections after a reconnect. Reference types need reference identity;
		// Steam connection handles are value types, boxed again on each receive.
		private sealed class TransferKeyComparer : IEqualityComparer<(object Source, int SequenceId)>
		{
			internal static bool SameSource(object a, object b) =>
				ReferenceEquals(a, b) || (a is ValueType && a.Equals(b));

			public bool Equals((object Source, int SequenceId) a, (object Source, int SequenceId) b) =>
				a.SequenceId == b.SequenceId && SameSource(a.Source, b.Source);

			public int GetHashCode((object Source, int SequenceId) key) =>
				unchecked(((key.Source is ValueType ? key.Source.GetHashCode() : RuntimeHelpers.GetHashCode(key.Source)) * 397) ^ key.SequenceId);
		}

		private static readonly Dictionary<(object Source, int SequenceId), PendingTransfer> Pending =
			new Dictionary<(object, int), PendingTransfer>(new TransferKeyComparer());
		private static readonly List<(object Source, int SequenceId)> Expired = new List<(object, int)>();
		private static int _pendingBytes;
		private static float _nextPrune;
		private static int _nextSequenceId;

		public void Serialize(BinaryWriter writer)
		{
			writer.Write(SequenceId);
			writer.Write(ChunkIndex);
			writer.Write(TotalChunks);
			writer.Write(ChunkData.Length);
			writer.Write(ChunkData);
		}

		public void Deserialize(BinaryReader reader)
		{
			SequenceId = reader.ReadInt32();
			ChunkIndex = reader.ReadInt32();
			TotalChunks = reader.ReadInt32();
			int len = reader.ReadInt32();
			if (TotalChunks <= 0 || TotalChunks > MaxChunksPerTransfer || ChunkIndex < 0 || ChunkIndex >= TotalChunks
				|| len <= 0 || len > MaxChunkBytes || len > reader.BaseStream.Length - reader.BaseStream.Position)
				throw new InvalidDataException("Invalid fragment header or length");
			ChunkData = reader.ReadBytes(len);
		}

		public void OnDispatched()
		{
			object source = PacketHandler.IncomingSource;
			if (source == null)
				throw new InvalidDataException("Fragment has no transport source");

			float now = Time.realtimeSinceStartup;
			PruneExpired(now);
			var key = (source, SequenceId);
			if (!Pending.TryGetValue(key, out var transfer))
			{
				// A full table used to drop the arriving transfer and keep the stale ones,
				// so once it filled with abandoned transfers every new payload was lost
				// until they aged out. The oldest incomplete transfer is the one least
				// likely to ever complete; it makes room for the new one.
				if (Pending.Count >= MaxPendingTransfers)
					EvictOldest();
				transfer = new PendingTransfer { Chunks = new byte[TotalChunks][], LastReceived = now };
				Pending.Add(key, transfer);
			}
			else if (transfer.Chunks.Length != TotalChunks)
			{
				Remove(key, transfer);
				throw new InvalidDataException("Fragment count changed within a transfer");
			}

			// Duplicate fragments must not advance completion or overwrite stored bytes.
			if (transfer.Chunks[ChunkIndex] != null)
				return;
			if (_pendingBytes + ChunkData.Length > MaxPendingBytes)
			{
				Remove(key, transfer);
				DebugConsole.LogAggregated("ChunkedPacket.ByteLimit", "[ChunkedPacket] Incomplete transfer memory limit reached; transfer dropped.");
				return;
			}
			transfer.Chunks[ChunkIndex] = ChunkData;
			transfer.ByteCount += ChunkData.Length;
			_pendingBytes += ChunkData.Length;
			transfer.LastReceived = now;
			if (++transfer.ReceivedCount != TotalChunks)
				return;

			Remove(key, transfer);
			byte[] fullData = new byte[transfer.ByteCount];
			int offset = 0;
			foreach (var chunk in transfer.Chunks)
			{
				Array.Copy(chunk, 0, fullData, offset, chunk.Length);
				offset += chunk.Length;
			}
			// Nested containers inherit the same transport source until dispatch returns.
			PacketHandler.HandleIncoming(fullData);
		}

		private static void Remove((object Source, int SequenceId) key, PendingTransfer transfer)
		{
			Pending.Remove(key);
			_pendingBytes -= transfer.ByteCount;
		}

		private static void EvictOldest()
		{
			float oldest = float.MaxValue;
			(object Source, int SequenceId) victim = default;
			PendingTransfer victimTransfer = null;
			foreach (var entry in Pending)
			{
				if (entry.Value.LastReceived < oldest)
				{
					oldest = entry.Value.LastReceived;
					victim = entry.Key;
					victimTransfer = entry.Value;
				}
			}
			if (victimTransfer == null)
				return;
			Remove(victim, victimTransfer);
			DebugConsole.LogAggregated("ChunkedPacket.TransferLimit", $"[ChunkedPacket] Reassembly table full; evicted the oldest incomplete transfer ({victimTransfer.ReceivedCount}/{victimTransfer.Chunks.Length} fragments).");
		}

		private static void PruneExpired(float now)
		{
			if (now < _nextPrune) return;
			_nextPrune = now + 1f;
			foreach (var entry in Pending)
				if (now - entry.Value.LastReceived > IdleTimeout)
					Expired.Add(entry.Key);
			foreach (var key in Expired)
				Remove(key, Pending[key]);
			if (Expired.Count > 0)
				DebugConsole.LogAggregated("ChunkedPacket.Expired", $"[ChunkedPacket] Discarded {Expired.Count} transfers idle for over {IdleTimeout}s.");
			Expired.Clear();
		}

		internal static void ForgetSource(object source)
		{
			foreach (var entry in Pending)
				if (TransferKeyComparer.SameSource(entry.Key.Source, source))
					Expired.Add(entry.Key);
			foreach (var key in Expired)
				Remove(key, Pending[key]);
			Expired.Clear();
		}

		internal static void ClearPending()
		{
			Pending.Clear();
			Expired.Clear();
			_pendingBytes = 0;
			_nextPrune = 0f;
		}

		public static int GetNextSequenceId()
		{
			return System.Threading.Interlocked.Increment(ref _nextSequenceId);
		}
	}
}
