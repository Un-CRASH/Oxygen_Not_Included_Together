using ONI_Together.Networking.Packets.Architecture;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ONI_Together.Networking.Transport
{
    /// <summary>
    /// Counters behind the "[NetStats]" lines the LiteNetLib server and client write
    /// every 30 s. Outgoing packets are counted by type where they enter the transport
    /// (TransportPacketSender.SendToConnection), so the line shows what the game
    /// produces, not only what the wire carries after coalescing. Sizes are known for
    /// coalesced entries, which are serialized on entry; other packets count without.
    /// </summary>
    internal static class NetStats
    {
        public const float ReportIntervalSeconds = 30f;

        private sealed class Counter
        {
            public int Count;
            public long Bytes;
        }

        private static readonly Dictionary<string, Counter> _outByType = new Dictionary<string, Counter>();
        private static readonly Dictionary<string, Counter> _syncFields = new();

        public static void RecordSyncFields(string behaviour, int fields, bool snapshot)
        {
            string key = (snapshot ? "snapshot/" : "delta/") + behaviour;
            if (!_syncFields.TryGetValue(key, out var counter))
                _syncFields[key] = counter = new Counter();
            counter.Count += fields;
        }

        private static int _outPackets;
        private static int _batches;
        private static int _batchedPackets;
        private static long _batchedBytes;
        private static int _direct;
        private static float _windowStart = -1f;

        public static void RecordOutgoing(IPacket packet, int bytes)
        {
            if (_windowStart < 0f)
                _windowStart = Time.realtimeSinceStartup;

            _outPackets++;
            string name = packet.GetType().Name;
            if (!_outByType.TryGetValue(name, out var counter))
                _outByType[name] = counter = new Counter();
            counter.Count++;
            counter.Bytes += bytes;
        }

        /// <summary>One CoalescedPacket left with this many entries and bytes.</summary>
        public static void RecordBatch(int packets, int bytes)
        {
            _batches++;
            _batchedPackets += packets;
            _batchedBytes += bytes;
        }

        /// <summary>A reliable packet left on its own: priority lane, or too large for a batch.</summary>
        public static void RecordDirect()
        {
            _direct++;
        }

        /// <summary>Bytes of a packet that was serialized after RecordOutgoing counted it without a size.</summary>
        public static void RecordBytes(IPacket packet, int bytes)
        {
            string name = packet.GetType().Name;
            if (!_outByType.TryGetValue(name, out var counter))
                _outByType[name] = counter = new Counter();
            counter.Bytes += bytes;
        }

        private static readonly Dictionary<string, Counter> _forcedReliable = new Dictionary<string, Counter>();

        /// <summary>An unreliable payload too large for one datagram went reliable instead.</summary>
        public static void RecordForcedReliable(IPacket packet, int bytes)
        {
            string name = packet.GetType().Name;
            if (!_forcedReliable.TryGetValue(name, out var counter))
                _forcedReliable[name] = counter = new Counter();
            counter.Count++;
            if (bytes > counter.Bytes) counter.Bytes = bytes;
        }

        private static int _backlogSkips;

        /// <summary>A periodic broadcast skipped one connection whose reliable channel was backlogged.</summary>
        public static void RecordBacklogSkip()
        {
            _backlogSkips++;
        }

        /// <summary>Forget the window; the session is over.</summary>
        public static void Reset()
        {
            _syncFields.Clear();
            _outByType.Clear();
            _outPackets = 0;
            _batches = 0;
            _batchedPackets = 0;
            _batchedBytes = 0;
            _direct = 0;
            _forcedReliable.Clear();
            _backlogSkips = 0;
            _windowStart = -1f;
        }

        /// <summary>The summary line for the log; starts a new window.</summary>
        public static string BuildReportAndReset()
        {
            float now = Time.realtimeSinceStartup;
            float seconds = _windowStart < 0f ? ReportIntervalSeconds : Mathf.Max(1f, now - _windowStart);

            var sb = new StringBuilder(512);
            sb.Append("[NetStats] outgoing over ").Append(seconds.ToString("0")).Append(" s: ")
              .Append(_outPackets).Append(" packets (").Append((_outPackets / seconds).ToString("0.0")).Append("/s), ")
              .Append(_batchedPackets).Append(" coalesced into ").Append(_batches).Append(" batches");
            if (_batches > 0)
            {
                sb.Append(" (avg ").Append((_batchedPackets / (float)_batches).ToString("0.0")).Append(" packets, ")
                  .Append(_batchedBytes / _batches).Append(" B)");
            }
            sb.Append(", ").Append(_direct).Append(" reliable sent alone; top types: ");

            var list = new List<KeyValuePair<string, Counter>>(_outByType);
            list.Sort((a, b) => b.Value.Count.CompareTo(a.Value.Count));
            int shown = 0;
            foreach (var kvp in list)
            {
                if (shown == 8)
                    break;
                if (shown > 0)
                    sb.Append(", ");
                shown++;
                sb.Append(kvp.Key).Append(' ').Append((kvp.Value.Count / seconds).ToString("0.0")).Append("/s");
                if (kvp.Value.Bytes > 0)
                    sb.Append(' ').Append((kvp.Value.Bytes / seconds / 1024f).ToString("0.0")).Append(" KB/s");
            }
            if (shown == 0)
                sb.Append("none");

            _outByType.Clear();
            _outPackets = 0;
            _batches = 0;
            _batchedPackets = 0;
            _batchedBytes = 0;
            _direct = 0;
            var syncList = new List<KeyValuePair<string, Counter>>(_syncFields);
            syncList.Sort((a, b) => b.Value.Count.CompareTo(a.Value.Count));
            sb.Append("; SyncVar fields: ");
            for (int i = 0; i < syncList.Count && i < 6; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(syncList[i].Key).Append(' ').Append(syncList[i].Value.Count);
            }
            if (syncList.Count == 0) sb.Append("none");
            _syncFields.Clear();
            if (_forcedReliable.Count > 0)
            {
                sb.Append("; oversize unreliable sent reliable: ");
                bool first = true;
                foreach (var kvp in _forcedReliable)
                {
                    if (!first) sb.Append(", ");
                    first = false;
                    sb.Append(kvp.Key).Append(' ').Append(kvp.Value.Count).Append(" (max ").Append(kvp.Value.Bytes).Append(" B)");
                }
                _forcedReliable.Clear();
            }
            if (_backlogSkips > 0)
            {
                sb.Append("; periodic broadcasts skipped for backlog: ").Append(_backlogSkips);
                _backlogSkips = 0;
            }
            _windowStart = now;
            return sb.ToString();
        }
    }
}
