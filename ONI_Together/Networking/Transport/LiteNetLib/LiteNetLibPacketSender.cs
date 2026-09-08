using LiteNetLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Profiling;
using System;

namespace ONI_Together.Networking.Transport.Lan
{
    public class LiteNetLibPacketSender : TransportPacketSender
    {
        public override bool SendPacket(object conn, IPacket packet, PacketSendMode sendType = PacketSendMode.ReliableImmediate)
        {
            using var _ = Profiler.Scope();

            if (conn is not NetPeer peer)
                return false;

            if (peer.ConnectionState != ConnectionState.Connected)
                return false;

            byte[] bytes = PacketSender.SerializePacketForSending(packet);
            NetStats.RecordBytes(packet, bytes.Length);

            if (packet is IPriorityPacket)
                sendType |= PacketSendMode.Priority;

            // LiteNetLib fragments reliable payloads itself and retransmits every fragment,
            // so nothing is split into ChunkedPackets here any more. The mod's own
            // fragments were sent with the payload's send mode, unreliable ones included:
            // one lost fragment left the transfer parked in the receiver's 64-slot
            // reassembly table for 120 s, and once that table was full every chunked
            // packet - reliable batches and save chunks too - was dropped for minutes.
            // A payload too large for one unreliable datagram cannot be delivered
            // unreliably at all, so it goes reliable instead.
            if (bytes.Length > MaxUnreliablePayloadBytes && (sendType & PacketSendMode.Reliable) == 0)
            {
                NetStats.RecordForcedReliable(packet, bytes.Length);
                sendType = (sendType | PacketSendMode.Reliable) & ~PacketSendMode.NoDelay;
            }

            return SendRaw(peer, bytes, packet, sendType);
        }

        /// <summary>
        /// Unreliable datagrams must fit the negotiated MTU (1024 here, minus headers);
        /// LiteNetLib throws for larger ones instead of fragmenting.
        /// </summary>
        private const int MaxUnreliablePayloadBytes = 1000;

        /// <summary>
        /// The reliable-ordered channel keeps 64 packets in flight; above this depth the
        /// periodic broadcasters skip a tick rather than pile more behind the backlog.
        /// </summary>
        private const int BacklogThreshold = 200;

        public override bool IsBacklogged(object connection) =>
            connection is NetPeer peer && peer.ConnectionState == ConnectionState.Connected
            && peer.GetPacketsCountInReliableQueue(DefaultChannel, false) > BacklogThreshold;

        private bool SendRaw(object conn, byte[] bytes, IPacket packet, PacketSendMode sendType)
        {
            if (conn is not NetPeer peer)
                return false;

            if (peer.ConnectionState != ConnectionState.Connected)
                return false;

            DeliveryMethod deliveryMethod = ConvertSendType(sendType, packet);
            byte channel = IsPriority(sendType, packet) ? PriorityChannel : DefaultChannel;

            // Test rig only (ONI_TOGETHER_TEST_LOSS): drop a share of unreliable datagrams
            // before they leave, to see what a lossy link does to the world state.
            if (deliveryMethod == DeliveryMethod.Unreliable && DebugTools.TestHarness.DropUnreliableChance > 0f
                && UnityEngine.Random.value < DebugTools.TestHarness.DropUnreliableChance)
            {
                DebugTools.TestHarness.CountDroppedUnreliable();
                return true;
            }

            try
            {
                peer.Send(bytes, channel, deliveryMethod);

                PacketTracker.TrackSent(new PacketTracker.PacketTrackData
                {
                    packet = packet,
                    size = bytes.Length
                });

                return true;
            }
            catch (Exception ex)
            {
                DebugConsole.LogError($"[LiteNetLibPacketSender] Failed to send packet {packet.GetType().Name} ({bytes.Length} bytes): " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// LiteNetLib orders reliable packets per channel, so a second channel is a lane the
        /// world stream cannot block. Both server and client open four (ChannelsCount = 4).
        /// Unreliable sends ignore the channel number; they never queue behind reliable
        /// traffic anyway.
        /// </summary>
        private const byte DefaultChannel = 0;
        private const byte PriorityChannel = 1;

        /// <summary>
        /// The reliable-ordered channel holds 64 packets in flight per round trip
        /// regardless of size, so the base class packs a frame's reliable packets into
        /// one (see TransportPacketSender).
        /// </summary>
        protected override bool SupportsCoalescing => true;

        public override bool CanSendSnapshot(object connection) =>
            base.CanSendSnapshot(connection) && connection is NetPeer peer
            && peer.ConnectionState == ConnectionState.Connected
            && peer.GetPacketsCountInReliableQueue(DefaultChannel, true) < 32;

        private static bool IsPriority(PacketSendMode sendType, IPacket packet)
        {
            return (sendType & PacketSendMode.Priority) != 0 || packet is IPriorityPacket;
        }

        private static DeliveryMethod ConvertSendType(PacketSendMode sendType, IPacket packet)
        {
            if ((sendType & PacketSendMode.Reliable) != 0)
                return DeliveryMethod.ReliableOrdered;

            if (packet is ILatencySensitivePacket || (sendType & PacketSendMode.NoDelay) != 0)
                return DeliveryMethod.Unreliable;

            return DeliveryMethod.Unreliable;
        }
    }
}
