using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Synchronization;
using System.IO;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.World
{
	/// <summary>
	/// The host's clock, sent once a second of real time (GameClockSync.HostTick).
	/// Unreliable: a lost one is replaced by the next, and LiteNetLib never queues an
	/// unreliable packet behind the reliable stream.
	///
	/// HostPaused tells the client that the host's clock is standing still, so the
	/// client must not read the growing gap as drift (see GameClockSync). It is the
	/// last field on the wire and is read only when present, so a host from before
	/// this field still talks to this client.
	/// </summary>
	public class WorldCyclePacket : IPacket, IPriorityPacket
	{
		public int Cycle { get; set; }
		public float CycleTime { get; set; }
		public bool HostPaused { get; set; }

		public void Serialize(BinaryWriter writer)
		{
			writer.Write(Cycle);
			writer.Write(CycleTime);
			writer.Write(HostPaused);
		}

		public void Deserialize(BinaryReader reader)
		{
			Cycle = reader.ReadInt32();
			CycleTime = reader.ReadSingle();
			HostPaused = reader.BaseStream.Position < reader.BaseStream.Length && reader.ReadBoolean();
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			GameClockSync.OnHostTime(Cycle, CycleTime, HostPaused);
		}
	}
}
