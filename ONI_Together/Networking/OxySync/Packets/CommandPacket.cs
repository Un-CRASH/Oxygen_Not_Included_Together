using System.IO;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.OxySync.Components;
using ONI_Together.Networking.Packets.Architecture;
using Shared.OxySync;
using Shared.Profiling;

namespace ONI_Together.Networking.OxySync.Packets
{
    public class CommandPacket : IPacket
    {
        public int NetId;
        public int BehaviourId;
        public int MethodHash;
        public byte[] Args;

        public CommandPacket()
        {
            Args = System.Array.Empty<byte>();
        }

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(NetId);
            writer.Write(BehaviourId);
            writer.Write(MethodHash);
            writer.Write(Args.Length);
            writer.Write(Args);
        }

        public void Deserialize(BinaryReader reader)
        {
            NetId = reader.ReadInt32();
            BehaviourId = reader.ReadInt32();
            MethodHash = reader.ReadInt32();
            int len = reader.ReadInt32();
            Args = reader.ReadBytes(len);
        }

        public void OnDispatched()
        {
            using var _ = Profiler.Scope();

            if (!MultiplayerSession.IsHost) return;

            // A behaviour that spawned before its identity registered sends with id 0,
            // which no lookup can answer; tens of thousands of "Lookup failed" per session.
            if (NetId == 0) return;

            var behaviour = OxySyncManager.ResolveBehaviour(NetId, BehaviourId);

            if (behaviour == null)
                return;

            behaviour.InvokeCommand(MethodHash, Args);
        }
    }
}