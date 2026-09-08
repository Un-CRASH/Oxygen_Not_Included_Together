using System.IO;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.OxySync.Components;
using ONI_Together.Networking.Packets.Architecture;
using Shared.OxySync;
using Shared.Profiling;

namespace ONI_Together.Networking.OxySync.Packets
{
    public class ClientRpcPacket : IPacket
    {
        public int NetId;
        public int BehaviourId;
        public int MethodHash;
        public byte[] Args;
        public ulong TargetPlayerId;

        public ClientRpcPacket()
        {
            Args = System.Array.Empty<byte>();
            TargetPlayerId = ulong.MaxValue;
        }

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(NetId);
            writer.Write(BehaviourId);
            writer.Write(MethodHash);
            writer.Write(Args.Length);
            writer.Write(Args);
            writer.Write(TargetPlayerId);
        }

        public void Deserialize(BinaryReader reader)
        {
            NetId = reader.ReadInt32();
            BehaviourId = reader.ReadInt32();
            MethodHash = reader.ReadInt32();
            int len = reader.ReadInt32();
            Args = reader.ReadBytes(len);
            TargetPlayerId = reader.ReadUInt64();
        }

        public void OnDispatched()
        {
            using var _ = Profiler.Scope();

            if (MultiplayerSession.IsHost) return;

            if (TargetPlayerId != ulong.MaxValue && TargetPlayerId != MultiplayerSession.LocalUserID)
                return;

            var behaviour = OxySyncManager.ResolveBehaviour(NetId, BehaviourId);
            
            if (behaviour == null)
                return;

            // A TargetRpc travels in this packet with the player id set; its handlers live
            // in the behaviour's TargetRpc table, not the ClientRpc one. Every full-state
            // position reply the host sent was looked up in the wrong table and dropped
            // without a word, so the clients re-asked twice a second per stale duplicant
            // for the whole session (the CommandPacket flood).
            if (TargetPlayerId != ulong.MaxValue)
                behaviour.InvokeTargetRpc(MethodHash, Args);
            else
                behaviour.InvokeClientRpc(MethodHash, Args);
        }
    }
}
