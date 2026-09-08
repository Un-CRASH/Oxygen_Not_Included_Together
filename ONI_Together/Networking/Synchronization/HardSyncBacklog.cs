using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Interfaces.Networking;
using UnityEngine;

namespace ONI_Together.Networking.Synchronization
{
	/// <summary>
	/// Host side: the world traffic a client cannot receive while it reloads the hard-sync
	/// save, held back and replayed once every client is in the new world.
	///
	/// A hard sync snapshots the host's world and hands it to the clients, and each
	/// client then leaves the session, loads the file (20-28 s per reload in the
	/// players' logs, five reloads in one evening) and comes back. The host, meanwhile,
	/// keeps broadcasting: what it sent while the client was still in its old world was
	/// applied to that world and lost with it; what it sent while the client was gone
	/// was never sent to anyone. Neither is in the snapshot, which was taken first. So
	/// every build and dig that finished, every deconstruction, priority change and
	/// sweep the host issued during the sync was permanently missing on the clients
	/// afterwards - the mechanism meant to remove divergence introduced a fresh set
	/// each time, about 110 s of orders over that session.
	///
	/// The host now stops broadcasting world traffic from the moment the sync starts,
	/// keeps the reliable packets serialized in order, and sends them to every client
	/// once all of them report ready in the new world. The connection-flow, save
	/// transfer and chat packets (PacketHandler.AllowedWithoutWorld) go through
	/// untouched. Unreliable packets are periodic state (positions, status items,
	/// cursors) that is resent anyway and are simply not captured. Packets addressed to
	/// one player or to an interest group are not captured either: the group traffic
	/// is SyncVar state the client requests in full after joining.
	/// </summary>
	public static class HardSyncBacklog
	{
		private const int MaxBytes = 16 * 1024 * 1024;
		private const int MaxPackets = 40000;
		/// <summary>A sync that never completes must not hold the world back forever.</summary>
		private const float MaxAgeSeconds = 600f;

		private struct Entry
		{
			public byte[] Data;
			public PacketSendMode Mode;
		}

		private static readonly Queue<Entry> _entries = new();
		private static readonly Dictionary<string, int> _byType = new();
		private static int _bytes;
		private static int _dropped;
		private static bool _active;
		private static float _since;

		public static bool IsActive => _active;
		public static int Count => _entries.Count;

		/// <summary>Host: a hard sync has begun; hold world traffic from here on.</summary>
		public static void Begin()
		{
			if (!MultiplayerSession.IsHost)
				return;
			if (_active)
				Discard();
			_active = true;
			_since = Time.unscaledTime;
			DebugConsole.Log("[HardSyncBacklog] Holding world traffic until every client has loaded the save");
		}

		/// <summary>
		/// True when the packet was taken into the backlog and must not be sent now.
		/// </summary>
		public static bool TryCapture(IPacket packet, PacketSendMode sendType)
		{
			if (!_active)
				return false;
			if (!MultiplayerSession.IsHost)
			{
				Discard();
				return false;
			}
			if ((sendType & PacketSendMode.Reliable) == 0)
				return false;
			if (packet is IViewportCullable)
				return false;
			var type = packet.GetType();
			if (PacketHandler.IsAllowedWithoutWorld(type))
				return false;
			if (Time.unscaledTime - _since > MaxAgeSeconds)
			{
				DebugConsole.LogWarning($"[HardSyncBacklog] The hard sync has not completed after {MaxAgeSeconds:F0} s; releasing the held traffic");
				Flush();
				return false;
			}

			byte[] data;
			try
			{
				data = PacketSender.SerializePacketForSending(packet);
			}
			catch (Exception ex)
			{
				DebugConsole.LogWarning($"[HardSyncBacklog] Could not serialize {type.Name} for the backlog: {ex.Message}");
				return false;
			}

			while (_entries.Count > 0 && (_bytes + data.Length > MaxBytes || _entries.Count >= MaxPackets))
			{
				_bytes -= _entries.Dequeue().Data.Length;
				_dropped++;
			}
			_entries.Enqueue(new Entry { Data = data, Mode = sendType });
			_bytes += data.Length;
			_byType.TryGetValue(type.Name, out int n);
			_byType[type.Name] = n + 1;
			return true;
		}

		/// <summary>Host: every client is in the new world; send what was held, in order.</summary>
		public static void Flush()
		{
			if (!_active)
				return;
			_active = false;

			int count = _entries.Count;
			int bytes = _bytes;
			int dropped = _dropped;
			string top = string.Join(", ", _byType.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key} {kv.Value}"));
			var entries = _entries.ToArray();
			ClearState();

			int failed = 0;
			foreach (var entry in entries)
			{
				try
				{
					using var ms = new MemoryStream(entry.Data);
					using var reader = new BinaryReader(ms);
					int type = reader.ReadInt32();
					var packet = PacketRegistry.Create(type);
					packet.Deserialize(reader);
					PacketSender.SendToAllClients(packet, entry.Mode);
				}
				catch (Exception ex)
				{
					failed++;
					if (failed <= 3)
						DebugConsole.LogWarning($"[HardSyncBacklog] Could not replay a held packet: {ex.Message}");
				}
			}

			DebugConsole.Log($"[HardSyncBacklog] Replayed {count} packets ({bytes / 1024} KB) held during the hard sync to every client; dropped {dropped}, failed {failed}; top types: {top}");
		}

		/// <summary>Host: the sync ended without a world to deliver to (no clients left, session over).</summary>
		public static void Discard()
		{
			if (_active || _entries.Count > 0)
			{
				string top = string.Join(", ", _byType.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key} {kv.Value}"));
				DebugConsole.Log($"[HardSyncBacklog] Discarded {_entries.Count} held packets ({_bytes / 1024} KB); top types: {top}");
			}
			_active = false;
			ClearState();
		}

		private static void ClearState()
		{
			_entries.Clear();
			_byType.Clear();
			_bytes = 0;
			_dropped = 0;
		}
	}
}
