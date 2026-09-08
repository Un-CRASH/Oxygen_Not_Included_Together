using System.Collections.Generic;
using System.IO;
using System.Linq;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Interfaces.Networking;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Synchronization
{
	/// <summary>
	/// Keeps the clients' natural terrain equal to the host's.
	///
	/// Cell contents had no state-based reconciliation at all: the host streams deltas
	/// against its own shadow grid, so a change a client missed and that then went
	/// static on the host - a dug-out tile, a tile that melted - was never re-derived,
	/// and a divergence the CLIENT's own simulation created was invisible to the host
	/// and could never be repaired. Both accumulate over a session, which is what the
	/// players saw as tiles "drifting apart" and getting worse with time.
	///
	/// The host walks the map in 32x32 chunks, a few per second, and sends one 32-bit
	/// digest per chunk over the cells that are natural solid terrain (solid and not a
	/// tile building): element per cell, nothing else. Liquids, gases and temperatures
	/// are left out on purpose - they legitimately differ between two simulations and
	/// the delta stream already carries them. A client whose own digest differs asks
	/// for that chunk, and the host answers with every cell of it; the client rewrites
	/// only the cells where it has natural solid terrain the host lacks, or lacks
	/// natural solid terrain the host has. A full pass over the players' map takes
	/// about half a minute; after a repair the digests agree and nothing more is sent.
	///
	/// Tile buildings are excluded on both sides (the BuildComplete path owns them):
	/// a cell that is a foundation tile on the host is never dug here, and a cell that
	/// is a foundation tile here is never rewritten.
	/// </summary>
	public static class TerrainReconcile
	{
		public const int ChunkSize = 32;
		public const int ChunksPerDigest = 8;
		public const float DigestInterval = 1f;
		/// <summary>A chunk is asked for again only after this long, whatever the digest says.</summary>
		public const float RequestCooldownSeconds = 60f;
		/// <summary>Chunks asked for per request, and the shortest gap between two requests (a chunk answer is ~10 KB).</summary>
		public const int MaxChunksPerRequest = 2;
		public const float RequestInterval = 2f;

		private static float _nextDigestAt;
		private static int _cursor;
		private static float _nextRequestAt;
		private static readonly Dictionary<int, float> _requestedAt = new();

		public static int DigestsSent, DigestsChecked, Mismatches, ChunksRequested, ChunksApplied, CellsRepaired;

		public static int ChunksX => (Grid.WidthInCells + ChunkSize - 1) / ChunkSize;
		public static int ChunksY => (Grid.HeightInCells + ChunkSize - 1) / ChunkSize;

		public static string Status => $"digests sent {DigestsSent}, checked {DigestsChecked}, mismatches {Mismatches}, chunks requested {ChunksRequested}, applied {ChunksApplied}, cells repaired {CellsRepaired}";

		public static void Reset()
		{
			_nextDigestAt = 0f;
			_cursor = 0;
			_nextRequestAt = 0f;
			_requestedAt.Clear();
		}

		private static bool WorldReady()
		{
			return Grid.WidthInCells > 0 && Game.Instance != null && Game.Instance.isSpawned;
		}

		/// <summary>Natural solid terrain: solid, and not a tile building.</summary>
		private static bool IsNaturalSolid(int cell)
		{
			return Grid.Solid[cell] && !Grid.Foundation[cell];
		}

		public static uint Digest(int cx, int cy)
		{
			uint hash = 2166136261u;
			int x0 = cx * ChunkSize, y0 = cy * ChunkSize;
			for (int y = y0; y < y0 + ChunkSize; y++)
			{
				for (int x = x0; x < x0 + ChunkSize; x++)
				{
					ushort v = 0;
					if (x < Grid.WidthInCells && y < Grid.HeightInCells)
					{
						int cell = Grid.XYToCell(x, y);
						if (Grid.IsValidCell(cell) && IsNaturalSolid(cell))
							v = (ushort)(Grid.ElementIdx[cell] + 1);
					}
					unchecked
					{
						hash = (hash ^ (uint)(v & 0xFF)) * 16777619u;
						hash = (hash ^ (uint)(v >> 8)) * 16777619u;
					}
				}
			}
			return hash;
		}

		// ---------------------------------------------------------------- host

		public static void HostTick()
		{
			if (!MultiplayerSession.IsHost || !MultiplayerSession.InActiveSession || !WorldReady())
				return;
			if (Time.unscaledTime < _nextDigestAt)
				return;
			_nextDigestAt = Time.unscaledTime + DigestInterval;

			int total = ChunksX * ChunksY;
			if (total == 0)
				return;

			var packet = new TerrainDigestPacket();
			for (int i = 0; i < ChunksPerDigest && i < total; i++)
			{
				int idx = _cursor++ % total;
				int cx = idx % ChunksX, cy = idx / ChunksX;
				packet.Entries.Add(new TerrainDigestPacket.Entry { Cx = (ushort)cx, Cy = (ushort)cy, Digest = Digest(cx, cy) });
			}
			DigestsSent++;
			PacketSender.SendToAllClientsUnlessBacklogged(packet, PacketSendMode.Reliable);
		}

		public static void OnRequest(TerrainChunkRequestPacket packet)
		{
			if (!MultiplayerSession.IsHost || !WorldReady())
				return;
			int sent = 0;
			foreach (var (cx, cy) in packet.Chunks)
			{
				if (sent >= MaxChunksPerRequest) break;
				if (cx >= ChunksX || cy >= ChunksY) continue;
				PacketSender.SendToPlayer(packet.SenderId, TerrainChunkPacket.Capture(cx, cy));
				sent++;
			}
			if (sent > 0)
				DebugConsole.LogAggregated("Terrain.Answer", $"[TerrainReconcile] sent {sent} terrain chunk(s) to {packet.SenderId} whose terrain differs there");
		}

		// -------------------------------------------------------------- client

		public static void OnDigest(TerrainDigestPacket packet)
		{
			if (!MultiplayerSession.IsClient || !WorldReady() || GameClient.IsHardSyncInProgress)
				return;

			List<(ushort, ushort)> wanted = null;
			foreach (var entry in packet.Entries)
			{
				DigestsChecked++;
				if (entry.Cx >= ChunksX || entry.Cy >= ChunksY) continue;
				if (Digest(entry.Cx, entry.Cy) == entry.Digest) continue;
				Mismatches++;
				int key = entry.Cx | (entry.Cy << 16);
				if (_requestedAt.TryGetValue(key, out float at) && Time.unscaledTime - at < RequestCooldownSeconds) continue;
				(wanted ??= new List<(ushort, ushort)>()).Add((entry.Cx, entry.Cy));
			}
			if (wanted == null)
				return;
			if (Time.unscaledTime < _nextRequestAt)
				return; // the next digest of these chunks comes round again
			_nextRequestAt = Time.unscaledTime + RequestInterval;

			var request = new TerrainChunkRequestPacket { SenderId = MultiplayerSession.LocalUserID };
			foreach (var chunk in wanted.Take(MaxChunksPerRequest))
			{
				_requestedAt[chunk.Item1 | (chunk.Item2 << 16)] = Time.unscaledTime;
				request.Chunks.Add(chunk);
			}
			ChunksRequested += request.Chunks.Count;
			DebugConsole.LogAggregated("Terrain.Request", $"[TerrainReconcile] terrain differs from the host in chunk(s) {string.Join(", ", request.Chunks.Select(c => $"({c.Item1},{c.Item2})"))}; asking for the host's cells");
			PacketSender.SendToHost(request);
		}

		public static void Apply(TerrainChunkPacket chunk)
		{
			if (!MultiplayerSession.IsClient || !WorldReady() || GameClient.IsHardSyncInProgress)
				return;
			if (chunk.Elements == null || chunk.Elements.Length != ChunkSize * ChunkSize)
				return;

			int x0 = chunk.Cx * ChunkSize, y0 = chunk.Cy * ChunkSize;
			int madeSolid = 0, dug = 0, element = 0;
			for (int i = 0; i < ChunkSize * ChunkSize; i++)
			{
				int x = x0 + i % ChunkSize, y = y0 + i / ChunkSize;
				if (x >= Grid.WidthInCells || y >= Grid.HeightInCells) continue;
				int cell = Grid.XYToCell(x, y);
				if (!Grid.IsValidCell(cell)) continue;
				if (Grid.Foundation[cell]) continue; // a tile building here: not terrain

				bool hostNatural = chunk.GetNatural(i);
				bool hostFoundation = chunk.GetFoundation(i);
				bool localNatural = Grid.Solid[cell];

				if (hostNatural)
				{
					if (!localNatural || Grid.ElementIdx[cell] != chunk.Elements[i])
					{
						SimMessages.ModifyCell(cell, chunk.Elements[i], SafeTemperature(chunk.Temperatures[i], chunk.Masses[i]), chunk.Masses[i], byte.MaxValue, 0, SimMessages.ReplaceType.Replace);
						if (!localNatural) madeSolid++; else element++;
					}
				}
				else if (!hostFoundation && localNatural)
				{
					// The host has no solid terrain here (dug out, melted, never was): ours
					// goes, and the cell takes what the host has in it.
					SimMessages.ModifyCell(cell, chunk.Elements[i], SafeTemperature(chunk.Temperatures[i], chunk.Masses[i]), chunk.Masses[i], byte.MaxValue, 0, SimMessages.ReplaceType.Replace);
					WorldDamage.Instance?.OnSolidStateChanged(cell);
					dug++;
				}
			}

			ChunksApplied++;
			CellsRepaired += madeSolid + dug + element;
			_requestedAt[chunk.Cx | (chunk.Cy << 16)] = Time.unscaledTime;
			DebugConsole.Log($"[TerrainReconcile] chunk ({chunk.Cx},{chunk.Cy}) from the host: {madeSolid} cell(s) made solid, {dug} dug out, {element} element changed");
		}

		private static float SafeTemperature(float temperature, float mass)
		{
			if (mass <= 0f) return 0f;
			if (temperature <= 1f || float.IsNaN(temperature) || float.IsInfinity(temperature)) return 293.15f;
			return temperature;
		}
	}

	/// <summary>Host -> clients: one digest per chunk of natural solid terrain.</summary>
	public class TerrainDigestPacket : IPacket
	{
		public struct Entry
		{
			public ushort Cx, Cy;
			public uint Digest;
		}

		public List<Entry> Entries = new();

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();
			writer.Write((byte)Entries.Count);
			foreach (var e in Entries)
			{
				writer.Write(e.Cx);
				writer.Write(e.Cy);
				writer.Write(e.Digest);
			}
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();
			int count = reader.ReadByte();
			Entries = new List<Entry>(count);
			for (int i = 0; i < count; i++)
				Entries.Add(new Entry { Cx = reader.ReadUInt16(), Cy = reader.ReadUInt16(), Digest = reader.ReadUInt32() });
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();
			TerrainReconcile.OnDigest(this);
		}
	}

	/// <summary>Client -> host: the chunks whose digest did not match here.</summary>
	public class TerrainChunkRequestPacket : IPacket
	{
		public ulong SenderId;
		public List<(ushort, ushort)> Chunks = new();

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();
			writer.Write(SenderId);
			writer.Write((byte)Chunks.Count);
			foreach (var (cx, cy) in Chunks)
			{
				writer.Write(cx);
				writer.Write(cy);
			}
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();
			SenderId = reader.ReadUInt64();
			int count = reader.ReadByte();
			Chunks = new List<(ushort, ushort)>(count);
			for (int i = 0; i < count; i++)
				Chunks.Add((reader.ReadUInt16(), reader.ReadUInt16()));
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();
			TerrainReconcile.OnRequest(this);
		}
	}

	/// <summary>Host -> one client: every cell of one chunk, with the natural-solid and foundation masks.</summary>
	public class TerrainChunkPacket : IPacket
	{
		private const int Cells = TerrainReconcile.ChunkSize * TerrainReconcile.ChunkSize;
		private const int MaskBytes = Cells / 8;

		public ushort Cx, Cy;
		public byte[] NaturalMask = new byte[MaskBytes];
		public byte[] FoundationMask = new byte[MaskBytes];
		public ushort[] Elements = new ushort[Cells];
		public float[] Masses = new float[Cells];
		public float[] Temperatures = new float[Cells];

		public bool GetNatural(int i) => (NaturalMask[i >> 3] & (1 << (i & 7))) != 0;
		public bool GetFoundation(int i) => (FoundationMask[i >> 3] & (1 << (i & 7))) != 0;

		public static TerrainChunkPacket Capture(int cx, int cy)
		{
			var packet = new TerrainChunkPacket { Cx = (ushort)cx, Cy = (ushort)cy };
			int size = TerrainReconcile.ChunkSize;
			int x0 = cx * size, y0 = cy * size;
			for (int i = 0; i < Cells; i++)
			{
				int x = x0 + i % size, y = y0 + i / size;
				if (x >= Grid.WidthInCells || y >= Grid.HeightInCells) continue;
				int cell = Grid.XYToCell(x, y);
				if (!Grid.IsValidCell(cell)) continue;
				packet.Elements[i] = Grid.ElementIdx[cell];
				packet.Masses[i] = Grid.Mass[cell];
				packet.Temperatures[i] = Grid.Temperature[cell];
				if (Grid.Foundation[cell])
					packet.FoundationMask[i >> 3] |= (byte)(1 << (i & 7));
				else if (Grid.Solid[cell])
					packet.NaturalMask[i >> 3] |= (byte)(1 << (i & 7));
			}
			return packet;
		}

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();
			writer.Write(Cx);
			writer.Write(Cy);
			writer.Write(NaturalMask);
			writer.Write(FoundationMask);
			for (int i = 0; i < Cells; i++)
			{
				writer.Write(Elements[i]);
				writer.Write(Masses[i]);
				writer.Write(Temperatures[i]);
			}
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();
			Cx = reader.ReadUInt16();
			Cy = reader.ReadUInt16();
			NaturalMask = reader.ReadBytes(MaskBytes);
			FoundationMask = reader.ReadBytes(MaskBytes);
			Elements = new ushort[Cells];
			Masses = new float[Cells];
			Temperatures = new float[Cells];
			for (int i = 0; i < Cells; i++)
			{
				Elements[i] = reader.ReadUInt16();
				Masses[i] = reader.ReadSingle();
				Temperatures[i] = reader.ReadSingle();
			}
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();
			TerrainReconcile.Apply(this);
		}
	}
}
