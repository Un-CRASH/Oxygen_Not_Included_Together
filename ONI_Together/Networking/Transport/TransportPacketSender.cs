using System;
using System.Collections.Generic;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;
using UnityEngine;

namespace ONI_Together.Networking.Transport
{
    public abstract class TransportPacketSender
    {
        private readonly Dictionary<object, Queue<(IPacket packet, PacketSendMode sendMode)>> _pendingQueues = new Dictionary<object, Queue<(IPacket packet, PacketSendMode sendMode)>>(ConnectionIdentityComparer.Instance);
        private readonly List<object> _emptyConnections = new List<object>();

        /// <summary>
        /// Reliable packets waiting to leave in one wire packet at the end of the frame.
        ///
        /// LiteNetLib's reliable-ordered channel allows 64 unacknowledged packets per
        /// round trip whatever their size, and the world stream is hundreds of small
        /// packets a second. Over the VPN the channel fell minutes behind: a building
        /// the host placed reached a client 5 to 9 minutes later, or never when a hard
        /// sync dropped the connection first. Packed into one CoalescedPacket per frame
        /// (up to MAX_PAYLOAD_BYTES), the same stream is a few dozen wire packets a
        /// second.
        ///
        /// Order is kept: entries leave in the order they were queued, a packet too
        /// large for a batch flushes what is queued before going alone, and the priority
        /// lane, unreliable sends and chunked payloads bypass the batch exactly as they
        /// bypassed the packet queue. Only a transport that says so batches
        /// (SupportsCoalescing); Steam does its own and neither it nor Riptide has the
        /// in-flight limit.
        /// </summary>
        private sealed class Batch
        {
            public List<byte[]> Entries = new List<byte[]>();
            public int Bytes = BatchHeaderBytes;
        }

        private const int BatchHeaderBytes = 8; // packet id + entry count
        private readonly Dictionary<object, Batch> _batches = new Dictionary<object, Batch>(ConnectionIdentityComparer.Instance);
        private readonly List<object> _flushedBatches = new List<object>();

        /// <summary>True for a transport whose reliable channel counts packets, not bytes.</summary>
        protected virtual bool SupportsCoalescing => false;

        // Full viewport snapshots wait behind an existing local queue. Ordinary
        // changes and world events keep their existing send order.
        public virtual bool CanSendSnapshot(object connection) =>
            !_pendingQueues.TryGetValue(connection, out var queue) || queue.Count == 0;

        public bool SendToConnection(object conn, IPacket packet, PacketSendMode sendType = PacketSendMode.ReliableImmediate)
        {
            // Never put latency-sensitive snapshots behind reliable state traffic.
            // Old movement data has no value and causes visible catch-up/teleports.
            // Session control (hard sync, ready, save transfer, pause, clock) must not
            // wait behind the world stream either; the transport's priority lane would
            // be pointless if it did.
            bool bypass = IsLatencySensitive(sendType)
                || packet is ILatencySensitivePacket
                || (sendType & PacketSendMode.Priority) != 0 || packet is IPriorityPacket;

            // Live option changes must not strand an existing queue or let newer
            // ordinary packets overtake it. Control/snapshot bypasses stay immediate.
            bool drainingQueue = _pendingQueues.TryGetValue(conn, out var pendingQueue) && pendingQueue.Count > 0;
            if (!bypass && !drainingQueue && ShouldCoalesce(packet, sendType))
            {
                Coalesce(conn, packet, sendType);
                return true;
            }

            // Coalescing switched off with a batch still pending (the option is live):
            // that batch goes first so no reliable packet overtakes it. Unreliable sends
            // have no order relative to the reliable channel and leave the batch alone.
            if (!bypass && (sendType & PacketSendMode.Reliable) != 0 && _batches.TryGetValue(conn, out var pending))
                FlushBatch(conn, pending);

            NetStats.RecordOutgoing(packet, 0);
            if ((sendType & PacketSendMode.Reliable) != 0)
                NetStats.RecordDirect();

            if (bypass || (!Configuration.Instance.EnablePacketQueue && !drainingQueue))
                return SendPacket(conn, packet, sendType);

            // queue it
            if (!_pendingQueues.TryGetValue(conn, out var queue))
                _pendingQueues[conn] = queue = new();

            // Given the nature of the game and the sync. I'm not sure this is a good idea for late game colonies
            //int MAX_QUEUE_DEPTH = 1000; // After 1000 packets. Discard oldest
            //if (queue.Count >= MAX_QUEUE_DEPTH)
            //    queue.Dequeue();

            queue.Enqueue((packet, sendType));
            return true;
        }

        internal static bool IsLatencySensitive(PacketSendMode sendType)
        {
            return (sendType & PacketSendMode.Reliable) == 0
                && (sendType & PacketSendMode.NoDelay) != 0;
        }

        private bool ShouldCoalesce(IPacket packet, PacketSendMode sendType)
        {
            return SupportsCoalescing
                && Configuration.Instance.CoalesceReliablePackets
                && (sendType & PacketSendMode.Reliable) != 0
                && packet is not ChunkedPacket
                && packet is not CoalescedPacket;
        }

        private void Coalesce(object conn, IPacket packet, PacketSendMode sendType)
        {
            byte[] entry = PacketSender.SerializePacketForSending(packet);
            NetStats.RecordOutgoing(packet, entry.Length);

            if (!_batches.TryGetValue(conn, out var batch))
                _batches[conn] = batch = new Batch();

            int cost = 4 + entry.Length;
            if (BatchHeaderBytes + cost > MAX_PAYLOAD_BYTES)
            {
                // Too large for a batch: it goes alone, after what is queued before it,
                // and SendPacket splits it into ChunkedPackets as before.
                FlushBatch(conn, batch);
                NetStats.RecordDirect();
                SendPacket(conn, packet, sendType);
                return;
            }

            if (batch.Bytes + cost > MAX_PAYLOAD_BYTES)
                FlushBatch(conn, batch);

            batch.Entries.Add(entry);
            batch.Bytes += cost;

            if (batch.Entries.Count >= CoalescedPacket.MaxEntries)
                FlushBatch(conn, batch);
        }

        private void FlushBatch(object conn, Batch batch)
        {
            if (batch.Entries.Count == 0)
                return;

            var container = new CoalescedPacket { Entries = batch.Entries };
            NetStats.RecordBatch(batch.Entries.Count, batch.Bytes);
            batch.Entries = new List<byte[]>();
            batch.Bytes = BatchHeaderBytes;
            SendPacket(conn, container, PacketSendMode.Reliable);
        }

        private void FlushBatches()
        {
            if (_batches.Count == 0)
                return;

            // Iterate a snapshot of the keys: a transport that ever re-entered the sender
            // would otherwise invalidate the enumerator, and one peer's failure must not
            // cost the others their frame.
            _flushedBatches.Clear();
            _flushedBatches.AddRange(_batches.Keys);
            foreach (var conn in _flushedBatches)
            {
                if (!_batches.TryGetValue(conn, out var batch))
                    continue;

                try
                {
                    FlushBatch(conn, batch);
                }
                catch (Exception ex)
                {
                    batch.Entries = new List<byte[]>();
                    batch.Bytes = BatchHeaderBytes;
                    DebugConsole.LogAggregated("Transport.FlushBatch", $"[TransportPacketSender] Sending a batch failed: {ex.Message}");
                }
            }

            // Forget the connections so a peer that left does not linger in the map.
            foreach (var key in _flushedBatches)
                _batches.Remove(key);
        }

        /// <summary>
        /// The packet queue's share for this tick, then this frame's batches. Called from
        /// NetworkingComponent.LateUpdate, and by the session stop paths before they close
        /// the connection.
        /// </summary>
        public void Flush()
        {
            if (_pendingQueues.Count > 0)
                FlushQueues();

            FlushBatches();
        }

        /// <summary>Drops everything waiting; the session is over.</summary>
        public void Discard()
        {
            _pendingQueues.Clear();
            _batches.Clear();
            _emptyConnections.Clear();
            _flushedBatches.Clear();
            PacketSender.ClearPending();
            NetStats.Reset();
        }

        private void FlushQueues()
        {
            int maxThisTick = (int)(Configuration.Instance.MaxPacketsPerSecond * Time.unscaledDeltaTime);
            //maxThisTick = Mathf.Clamp(maxThisTick, 1, 60); // never more than 60 per frame
            if (maxThisTick < 1) maxThisTick = 1;

            _emptyConnections.Clear();
            foreach (var kvp in _pendingQueues)
            {
                int sent = 0;
                while (kvp.Value.Count > 0 && sent < maxThisTick)
                {
                    var (packet, sendType) = kvp.Value.Dequeue();
                    SendPacket(kvp.Key, packet, sendType);
                    sent++;
                }
                if (kvp.Value.Count == 0)
                    _emptyConnections.Add(kvp.Key);
            }

            foreach (var key in _emptyConnections)
                _pendingQueues.Remove(key);
        }

        public abstract bool SendPacket(object conn, IPacket packet, PacketSendMode sendType = PacketSendMode.ReliableImmediate);

        /// <summary>True while this connection's reliable channel is far behind (see IsBacklogged overrides).</summary>
        public virtual bool IsBacklogged(object conn) => false;

        /// <summary>Reliable packets ride the per-frame CoalescedPacket on this transport, in send order.</summary>
        public bool CoalescesReliable => SupportsCoalescing && Configuration.Instance.CoalesceReliablePackets;

        private const int MAX_PAYLOAD_BYTES = 1000;

        /// <summary>
        /// Payloads larger than MAX_PAYLOAD_BYTES are split into ChunkedPacket fragments before hitting the transport, so backends
        /// with a hard single-message cap (e.g. LiteNetLib's 1023-byte unreliable limit or Riptides 1024-byte limit)
        /// never throw. sendRaw is provided by the transport to emit one serialized payload.
        /// </summary>
        protected bool SendChunkedIfNeeded(object conn, byte[] bytes, IPacket packet, PacketSendMode sendType, Func<object, byte[], IPacket, PacketSendMode, bool> sendRaw)
        {
            if (bytes.Length <= MAX_PAYLOAD_BYTES || packet is ChunkedPacket)
                return sendRaw(conn, bytes, packet, sendType);

            int chunkDataSize = MAX_PAYLOAD_BYTES - 20; // overhead for ChunkedPacket header
            int totalChunks = (bytes.Length + chunkDataSize - 1) / chunkDataSize;
            int sequenceId = ChunkedPacket.GetNextSequenceId();

            // A fragmented payload has no unreliable form: one lost fragment loses the
            // whole transfer and parks it in the receiver's reassembly table until it
            // expires. Every fragment goes reliable, whatever the payload asked for.
            if ((sendType & PacketSendMode.Reliable) == 0)
            {
                NetStats.RecordForcedReliable(packet, bytes.Length);
                sendType = (sendType | PacketSendMode.Reliable) & ~PacketSendMode.NoDelay;
            }

            for (int i = 0; i < totalChunks; i++)
            {
                int offset = i * chunkDataSize;
                int length = Math.Min(chunkDataSize, bytes.Length - offset);
                byte[] chunkData = new byte[length];
                Array.Copy(bytes, offset, chunkData, 0, length);

                var chunk = new ChunkedPacket
                {
                    SequenceId = sequenceId,
                    ChunkIndex = i,
                    TotalChunks = totalChunks,
                    ChunkData = chunkData
                };

                byte[] chunkBytes = PacketSender.SerializePacketForSending(chunk);
                if (!sendRaw(conn, chunkBytes, chunk, sendType))
                    return false;
            }

            return true;
        }
    }
}
