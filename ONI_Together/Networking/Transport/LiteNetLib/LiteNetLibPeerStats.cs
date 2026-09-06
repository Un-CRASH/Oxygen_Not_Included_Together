using LiteNetLib;
using System;
using UnityEngine;

namespace ONI_Together.Networking.Transport.Lan
{
    /// <summary>
    /// The per-peer part of the "[NetStats]" line: LiteNetLib's counters are lifetime
    /// totals, so each report is a difference of two samples, clamped at zero because a
    /// reconnected peer starts its counters over.
    /// </summary>
    internal static class LiteNetLibPeerStats
    {
        public struct Sample
        {
            public long Sent, Received, BytesSent, BytesReceived, Lost;
            public float At;
        }

        public static Sample Take(NetPeer peer, float now)
        {
            var s = peer.Statistics;
            return new Sample
            {
                Sent = s.PacketsSent,
                Received = s.PacketsReceived,
                BytesSent = s.BytesSent,
                BytesReceived = s.BytesReceived,
                Lost = s.PacketLoss,
                At = now
            };
        }

        public static string Describe(NetPeer peer, Sample prev, Sample cur)
        {
            float dt = Mathf.Max(1f, cur.At - prev.At);
            // No loss figure: LiteNetLib's PacketLoss counts every unacknowledged slot each
            // time an ack arrives, so it grows with queue depth rather than with loss.
            long sent = Math.Max(0, cur.Sent - prev.Sent);
            return $"rtt {peer.RoundTripTime} ms, mtu {peer.Mtu}, " +
                $"reliable queue {peer.GetPacketsCountInReliableQueue(0, true)} (priority lane {peer.GetPacketsCountInReliableQueue(1, true)}), " +
                $"sent {sent / dt:0.0}/s {Math.Max(0, cur.BytesSent - prev.BytesSent) / dt / 1024f:0.0} KB/s, " +
                $"received {Math.Max(0, cur.Received - prev.Received) / dt:0.0}/s {Math.Max(0, cur.BytesReceived - prev.BytesReceived) / dt / 1024f:0.0} KB/s";
        }
    }
}
