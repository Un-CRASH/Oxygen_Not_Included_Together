using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.World;
using Shared.Profiling;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ONI_Together.Networking.Components
{
    public class StatusBroadcaster : KMonoBehaviour, IRender200ms
    {
        public static readonly HashSet<int> SubscribedNetIds = new();
        public static readonly HashSet<int> PendingImmediate = new();

        private const float SoftSyncInterval = 0.5f;
        private const float HardSyncInterval = 5f;

        [MyCmpGet] private NetworkIdentity identity;
        [MyCmpGet] private KSelectable selectable;

        private float timeSinceLastSoftSync;
        private float timeSinceHardSync;

        public override void OnSpawn()
        {
            using var _ = Profiler.Scope();
            base.OnSpawn();
            timeSinceLastSoftSync = 0f;
            timeSinceHardSync = 0f;
        }

        public void Render200ms(float dt)
        {
            using var _ = Profiler.Scope();
            if (!MultiplayerSession.IsHostInSession) return;
            if (identity == null || selectable == null) return;

            timeSinceLastSoftSync += dt;
            timeSinceHardSync += dt;

            bool isSubscribed = SubscribedNetIds.Contains(identity.NetId);
            bool doSoftSync = isSubscribed && timeSinceLastSoftSync >= SoftSyncInterval;
            bool doHardSync = timeSinceHardSync >= HardSyncInterval;
            bool immediate = PendingImmediate.Remove(identity.NetId);

            if (immediate || doSoftSync || doHardSync)
            {
                if (doSoftSync || immediate) timeSinceLastSoftSync = 0f;
                if (doHardSync) timeSinceHardSync = 0f;

                try
                {
                    BroadcastSnapshot();
                }
                catch (Exception ex)
                {
                    DebugConsole.LogError($"[DuplicantStatusBroadcaster] Failed to broadcast for dupe {identity.NetId}: {ex}");
                }
            }
        }

        private void BroadcastSnapshot()
        {
            using var _ = Profiler.Scope();

            int cell = Grid.PosToCell(transform.position);
            if (!WorldStateSyncer.Instance.IsCellVisibleToAnyClientViewport(cell, margin: 4))
                return;

            var group = selectable.GetStatusItemGroup();
            if (group == null) return;

            var packet = new StatusItemsPacket { DupeNetId = identity.NetId };
            foreach (var entry in group)
            {
                if (packet.Entries.Count >= StatusItemsPacket.MaxEntries)
                    break;

                packet.Entries.Add(new StatusItemEntry
                {
                    ItemId = entry.item?.Id ?? string.Empty,
                    CategoryId = entry.category?.Id,
                    DisplayName = entry.GetName(),
                    Tooltip = Truncate(entry.item?.GetTooltip(entry.data), MaxTooltipChars),
                });
            }

            // Unreliable payloads must fit one datagram; with the localized tooltips a
            // duplicant's list ran to 1-3 KB and was fragmented (and, before the fragment
            // fix, lost). Trailing entries are the least visible ones in the panel.
            PacketSizeGuard.TrimToBudget(packet, packet.Entries);
            PacketSender.SendToAllClientsUnlessBacklogged(packet, PacketSendMode.Unreliable);
        }

        private const int MaxTooltipChars = 200;

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Length <= max ? text : text.Substring(0, max);
        }
    }
}
