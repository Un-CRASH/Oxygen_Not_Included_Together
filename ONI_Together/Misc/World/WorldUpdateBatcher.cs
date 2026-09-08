using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.World;
using System;
using System.Collections.Generic;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Misc.World
{
	public static class WorldUpdateBatcher
	{
		private static readonly List<WorldUpdatePacket.CellUpdate> pendingUpdates = new List<WorldUpdatePacket.CellUpdate>();
		private static float flushTimer = 0f;
		// Cell changes are the dig and tile results the clients wait for; ten seconds of
		// batching was visible as tiles lagging behind the duplicants.
		private const float FlushInterval = 2f; // Seconds

		public static void Queue(WorldUpdatePacket.CellUpdate update)
		{
			using var _ = Profiler.Scope();

			if(MultiplayerSession.IsClient)
			{
				// Client is not allowed to send WorldUpdate states as the host has full authority
				return;
			}

			lock (pendingUpdates)
			{
				pendingUpdates.Add(update);
			}
		}

		public static void Update()
		{
			using var _ = Profiler.Scope();

			if (MultiplayerSession.IsClient)
			{
				return;
			}

			flushTimer += Time.unscaledDeltaTime;
			if (flushTimer >= FlushInterval)
			{
				Flush();
				flushTimer = 0f;
			}
		}

        public static int Flush()
        {
	        using var _ = Profiler.Scope();

            if (MultiplayerSession.IsClient)
            {
                return 0;
            }

            // One packet must stay under the transport's single-datagram limit (1000 B):
            // above it the payload is fragmented, and a fragmented cell batch either
            // costs several reliable packets or, before the fragment fix, was lost
            // whole. Deflate on a cell run measures 12.6-14.4 B per update; the old
            // estimate of 5.38 B filled every batch to ~1.3 KB.
            const float maxPacketSize = 900f;
            const int PacketHeaderSize = 12;
            const float BytesPerUpdate = 16f;

            lock (pendingUpdates)
            {
                if (pendingUpdates.Count == 0)
                    return 0;

                int totalUpdates = pendingUpdates.Count;

                List<WorldUpdatePacket.CellUpdate> currentBatch = new List<WorldUpdatePacket.CellUpdate>();
                float currentSize = PacketHeaderSize;

                for (int i = 0; i < pendingUpdates.Count; i++)
                {
                    var update = pendingUpdates[i];

                    // If adding this update would exceed packet size -> flush current batch
                    if (currentSize + BytesPerUpdate > maxPacketSize)
                    {
                        if (currentBatch.Count > 0)
                        {
                            var packet = new WorldUpdatePacket();
                            packet.Updates.AddRange(currentBatch);

                            Send(packet);

                            currentBatch.Clear();
                            currentSize = PacketHeaderSize;
                        }
                    }

                    currentBatch.Add(update);
                    currentSize += BytesPerUpdate;
                }

                // Flush remaining
                if (currentBatch.Count > 0)
                {
                    var packet = new WorldUpdatePacket();
                    packet.Updates.AddRange(currentBatch);

                    Send(packet);
                }

                pendingUpdates.Clear();

                return (int)(totalUpdates * BytesPerUpdate);
            }
        }

        /// <summary>
        /// Reliable: a cell update is the result of a dig or a build and is sent once. The
        /// sender marks the cell as reported when it queues it (WorldStateSyncer's shadow
        /// grids), so a lost unreliable batch left the client's tile wrong until the next
        /// hard sync. The coalescer packs these with the rest of the world stream.
        /// </summary>
        private static void Send(WorldUpdatePacket packet)
        {
            PacketSender.SendToAllClients(packet, sendType: PacketSendMode.Reliable);
        }

    }
}
