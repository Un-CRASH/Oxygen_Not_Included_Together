using HarmonyLib;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets
{
	internal class API_Helper
	{
		private static readonly ConcurrentDictionary<Type, int> PacketHashes = new();

		public static Type CreateModApiPacketType(Type modPacketType)
		{
			using var _ = Profiler.Scope();

			var genericType = typeof(ModApiPacket<>);
			var constructedType = genericType.MakeGenericType(modPacketType);
			return constructedType;
		}

		public static int GetHashCode(Type type)
		{
			using var _ = Profiler.Scope();

			// Type identity is immutable. Hash once, including sends from worker threads.
			return PacketHashes.GetOrAdd(type, ComputePacketHash);
		}

		private static int ComputePacketHash(Type type)
		{
			var identity = type.FullName!;
			using var sha256 = SHA256.Create();
			var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(identity));
			return BitConverter.ToInt32(bytes, 0);
		}

		public static bool WrapApiPacket(object packet, out IPacket wrap)
		{
			using var _ = Profiler.Scope();

			wrap = null;

			var type = packet.GetType();
			int id = GetHashCode(type);
			if (!PacketRegistry.HasRegisteredPacket(type))
				return false;
			wrap = PacketRegistry.Create(id);
			if(wrap is IModApiPacket apiPacket)
			{
				apiPacket.SetWrappedInstance(packet);
				return true;
			}
			return false;
		}

		public static bool ValidAsModApiPacket(Type potentialPacketType)
		{
			using var _ = Profiler.Scope();

			///Ducktyping check if it has the required methods from IPacket interface
			var t = Traverse.Create(potentialPacketType);
			if (t.Method("Serialize", [typeof(BinaryWriter)]).MethodExists() &&
				t.Method("Deserialize", [typeof(BinaryReader)]).MethodExists() &&
				t.Method("OnDispatched").MethodExists())
				return true;
			return false;
		}

	}
}
