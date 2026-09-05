using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Synchronization;
using System.IO;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.World
{
	/// <summary>
	/// The host's clock, sent once a second of real time (GameClockSync.HostTick).
	/// Unreliable and on the priority lane: a lost one is replaced by the next, and
	/// none of them waits behind the world stream.
	/// </summary>
	public class WorldCyclePacket : IPacket, IPriorityPacket
	{
		public int Cycle { get; set; }
		public float CycleTime { get; set; }

		public void Serialize(BinaryWriter writer)
		{
			writer.Write(Cycle);
			writer.Write(CycleTime);
		}

		public void Deserialize(BinaryReader reader)
		{
			Cycle = reader.ReadInt32();
			CycleTime = reader.ReadSingle();
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			GameClockSync.OnHostTime(Cycle, CycleTime);
		}
	}
}
