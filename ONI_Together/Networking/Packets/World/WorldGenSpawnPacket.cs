using System.IO;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Synchronization;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.World
{
	/// <summary>
	/// Host -> clients: "my WorldGenSpawner just created PrefabTag at Cell under NetId".
	///
	/// No object is spawned on receipt. The client's own WorldGenSpawner creates the
	/// same object when its fog of war lifts there; WorldGenSpawnMap pairs the two.
	/// See that class for the crash this replaces.
	/// </summary>
	public class WorldGenSpawnPacket : IPacket
	{
		public int NetId;
		public string PrefabTag = string.Empty;
		public int Cell;

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(NetId);
			writer.Write(PrefabTag ?? string.Empty);
			writer.Write(Cell);
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			NetId = reader.ReadInt32();
			PrefabTag = reader.ReadString();
			Cell = reader.ReadInt32();
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (MultiplayerSession.IsHost) return;

			WorldGenSpawnMap.OnHostMapping(NetId, PrefabTag, Cell);
		}
	}
}
