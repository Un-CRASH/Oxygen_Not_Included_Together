using System;
using System.IO;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Overlay;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.Packets.Handshake;
using ONI_Together.Networking.Packets.Social;
using ONI_Together.Networking.Packets.Tools;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Misc;
using System.Collections.Generic;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Packets.Architecture
{

	public static class PacketHandler
	{
		private static bool _readyToProcess = true;

		// Transport metadata, never a player id claimed by an incoming packet. Nested
		// Bulk/Coalesced/Chunked dispatches inherit it. Restore it even after a failure.
		[ThreadStatic] private static object _incomingSource;
		internal static object IncomingSource => _incomingSource;

		public static void HandleIncoming(byte[] data, object source)
		{
			if (source == null) throw new ArgumentNullException(nameof(source));
			object previous = _incomingSource;
			_incomingSource = source;
			try { HandleIncoming(data); }
			finally { _incomingSource = previous; }
		}

		internal static void ForgetSource(object source) => ChunkedPacket.ForgetSource(source);

		/// <summary>
		/// A client in the frontend receives the host's live broadcasts before it has a world, and
		/// they throw there (Grid is 0x0). It only runs IAllowedWithoutWorldPacket packets and mod
		/// API packets until the world loads; the rest is dropped, the save carries that state.
		/// </summary>
		public static bool ShouldDispatchWithoutWorld(IPacket packet)
		{
			if (MultiplayerSession.IsHost) return true;
			if (!Utils.IsInMenu()) return true;
			return packet is IAllowedWithoutWorldPacket || packet is IModApiPacket;
		}

		/// <summary>A connection-flow packet: never held back during a hard sync.</summary>
		public static bool IsAllowedWithoutWorld(Type type) => typeof(IAllowedWithoutWorldPacket).IsAssignableFrom(type);

		private static float _notReadySince = float.MaxValue;
		private const float NOT_READY_TIMEOUT = 60f;

		public static bool readyToProcess
		{
			get => _readyToProcess;
			set
			{
				if (!value)
					_notReadySince = Time.unscaledTime;
				_readyToProcess = value;
			}
		}

		public static void HandleIncoming(byte[] data)
		{
			using var _ = Profiler.Scope();

			if (!_readyToProcess)
			{
				if (Time.unscaledTime - _notReadySince > NOT_READY_TIMEOUT)
				{
					DebugConsole.LogWarning($"[PacketHandler] readyToProcess was false for >{NOT_READY_TIMEOUT}s — force-recovering");
					_readyToProcess = true;
				}
				else
				{
					return;
				}
			}

			using (var ms = new MemoryStream(data))
			{
				using (var reader = new BinaryReader(ms))
				{
					int type = (int)reader.ReadInt32();
                    if (!PacketRegistry.HasRegisteredPacket(type))
                    {
                        DebugConsole.LogError($"Invalid PacketType received: {type}", false);
                        return;
                    }

                    using var scope = Profiler.Scope();

                    var packet = PacketRegistry.Create(type);
					packet.Deserialize(reader);

					if (!ShouldDispatchWithoutWorld(packet))
						return;

					Dispatch(packet);

                    scope.End(packet.GetType().Name, data.Length);

                    PacketTracker.TrackIncoming(new PacketTracker.PacketTrackData
                    {
						packet = packet,
						size = data.Length
                    });

					var tracker = NetIdActivityTracker.Instance;
					if (tracker != null)
					{
						int netId = NetIdActivityTracker.GetNetIdFromPacket(packet);
						if (netId > 0)
							tracker.RecordActivity(netId, data.Length);
					}
                }
			}
		}

		private static void Dispatch(IPacket packet)
		{
			using var _ = Profiler.Scope();

			packet.OnDispatched();
		}
	}

}