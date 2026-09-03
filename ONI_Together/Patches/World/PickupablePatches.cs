using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.World;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Patches.World
{
	public static class PickupablePatches
	{
        /// <summary>
        /// Living things and markers are synced by other means; only loose items go
        /// through the ground-item packets.
        /// </summary>
        public static bool IsGroundItemCandidate(Pickupable pickupable)
        {
            if (pickupable == null || pickupable.gameObject == null)
                return false;

            return pickupable.GetComponent<CreatureBrain>() == null &&
                   pickupable.GetComponent<Health>() == null &&
                   pickupable.GetComponent<MinionIdentity>() == null &&
                   !pickupable.name.Contains("TargetLocator");
        }

        /// <summary>
        /// Tells every client to create this pickupable. Shared by the spawn patch
        /// below and by the container drop patches in StoragePatches: an item that
        /// came into being inside a container was skipped by the spawn patch
        /// (correctly - container contents are synced as a blob), so when it is
        /// dropped on the floor the clients have never heard of it.
        /// </summary>
        public static void AnnounceToClients(Pickupable pickupable)
        {
            using var _ = Profiler.Scope();

            if (!MultiplayerSession.IsHost || !MultiplayerSession.InActiveSession)
                return;
            if (Game.Instance == null || !Game.Instance.isSpawned || GameServerHardSync.IsHardSyncInProgress)
                return;
            if (!IsGroundItemCandidate(pickupable))
                return;

            var identity = pickupable.gameObject.GetNetIdentity();
            if (identity == null)
                return;
            if (identity.NetId == 0)
                identity.RegisterIdentity();
            if (identity.NetId == 0)
                return;

            // Check if it is a substance resource (ore, dirt, liquid, gas chunk)
            var pe = pickupable.GetComponent<PrimaryElement>();
            bool isSubstance = pe != null && pe.Element != null && pe.Mass > 0f &&
                               pe.ElementID != SimHashes.Creature &&
                               pe.ElementID != SimHashes.Void &&
                               pickupable.GetComponent<SubstanceChunk>() != null;

            if (isSubstance)
            {
                var packet = new SpawnPrefabPacket(
                    identity.NetId,
                    pe.Element.id.GetHashCode(),
                    pickupable.transform.position,
                    pe.Mass,
                    pe.Temperature,
                    pe.DiseaseIdx,
                    pe.DiseaseCount,
                    pe.Element.id.ToString()
                );
                PacketSender.SendToAllClients(packet);
            }
            else
            {
                var tag = pickupable.PrefabID();
                var packet = new SpawnPrefabPacket(
                    identity.NetId,
                    tag.GetHashCode(),
                    pickupable.transform.position,
                    tag.Name
                )
                {
                    IsActive = pickupable.gameObject.activeSelf
                };
                PacketSender.SendToAllClients(packet);
            }
        }

        [HarmonyPatch(typeof(Pickupable), nameof(Pickupable.OnSpawn))]
        public static class PickupableOnSpawnPatch
        {
            public static void Postfix(Pickupable __instance)
            {
                using var _ = Profiler.Scope();
                try
                {
                    if (!IsGroundItemCandidate(__instance))
                        return;

                    var identity = __instance.gameObject.GetNetIdentity();

                    if (!MultiplayerSession.IsHost || !MultiplayerSession.InActiveSession)
                        return;

                    // Skip during world loading / save deserialization / hard sync
                    if (Game.Instance == null || !Game.Instance.isSpawned || GameServerHardSync.IsHardSyncInProgress)
                        return;

                    // Skip if triggered by packet dispatch
                    if (SpawnPrefabPacket.ProcessingIncoming || WorldDamageSpawnResourcePacket.ProcessingIncoming)
                        return;

                    // Skip if already in container/storage
                    if (__instance.storage != null)
                        return;

                    if (identity == null)
                        return;

                    AnnounceToClients(__instance);
                }
                catch (System.Exception ex)
                {
                    DebugConsole.LogError($"[PickupableOnSpawnPatch] Exception: {ex}");
                }
            }
        }

        // `TakeUnit` is not patched separately: it delegates to `Take`, so a patch on it
        // would send a second packet for the same pickup.

        [HarmonyPatch(typeof(Pickupable), nameof(Pickupable.Take))]
        public static class PickupableTakePatch
        {
            public static void Postfix(Pickupable __instance, Pickupable __result)
            {
                using var _ = Profiler.Scope();
                try
                {
                    if (!MultiplayerSession.IsHost || !MultiplayerSession.InActiveSession)
                        return;

                    if (__instance == null)
                        return;

                    // nothing was taken
                    if (__result == null)
                        return;

                    if (__instance.GetComponent<CreatureBrain>() != null ||
                        __instance.GetComponent<Health>() != null ||
                        __instance.GetComponent<MinionIdentity>() != null)
                        return;

                    var identity = __instance.GetNetIdentity();
                    if (identity == null || identity.NetId == 0)
                        return;

                    // A partial take splits off a new pickupable and leaves the source stack alive.
                    // A full take returns the source itself.
                    //
                    // The old guard here was `TotalAmount > 0f` -> return, which announced only
                    // the takes that drained the stack. A partial take was never sent, so the
                    // client kept the stack at its old size until something else corrected it.
                    bool sourceRemains = __result != __instance;
                    PacketSender.SendToAllClients(new PickupItemPacket
                    {
                        NetId = identity.NetId,
                        UnitsTaken = __result.TotalAmount,
                        UnitsRemaining = sourceRemains ? __instance.TotalAmount : 0f,
                        SourceRemains = sourceRemains,
                    });
                }
                catch (System.Exception ex)
                {
                    DebugConsole.LogError($"[PickupableTakePatch] Exception: {ex}");
                }
            }
        }

        /// <summary>
        /// See PickupableMergePacket. Runs after Absorb, so the absorber already holds
        /// the merged amount; the absorbed one is queued for destruction but still
        /// readable this frame. The plain destroy for it is suppressed in the
        /// clean-up patch below, since it would race this and lose the mass.
        /// </summary>
        [HarmonyPatch(typeof(Pickupable), nameof(Pickupable.Absorb))]
        public static class PickupableAbsorbPatch
        {
            public static void Postfix(Pickupable __instance, Pickupable pickupable)
            {
                using var _ = Profiler.Scope();
                try
                {
                    if (!MultiplayerSession.IsHost || !MultiplayerSession.InActiveSession)
                        return;
                    if (Game.Instance == null || !Game.Instance.isSpawned || GameServerHardSync.IsHardSyncInProgress)
                        return;
                    if (__instance == null || pickupable == null)
                        return;
                    if (!IsGroundItemCandidate(__instance))
                        return;

                    var absorberIdentity = __instance.gameObject.GetExistingNetIdentity();
                    var absorbedIdentity = pickupable.gameObject.GetExistingNetIdentity();
                    int absorberNetId = absorberIdentity != null ? absorberIdentity.NetId : 0;
                    int absorbedNetId = absorbedIdentity != null ? absorbedIdentity.NetId : 0;
                    if (absorberNetId == 0 && absorbedNetId == 0)
                        return;

                    PacketSender.SendToAllClients(new PickupableMergePacket
                    {
                        AbsorberNetId = absorberNetId,
                        AbsorbedNetId = absorbedNetId,
                        AbsorberUnits = __instance.TotalAmount,
                        AbsorberOnGround = __instance.storage == null,
                    });
                }
                catch (System.Exception ex)
                {
                    DebugConsole.LogError($"[PickupableAbsorbPatch] Exception: {ex}");
                }
            }
        }

        /// <summary>
        /// Clients do not merge ground stacks on their own. Two stacks landing in one
        /// cell merge on the host, and the host says so (PickupableMergePacket); a
        /// client merging them itself as well could pick the other survivor and end
        /// up deleting both once the message from the host arrives. Container-side
        /// merges, where allow_cross_storage is set, stay: the client rebuilds
        /// container contents through Store and relies on them stacking.
        /// </summary>
        [HarmonyPatch(typeof(Pickupable), nameof(Pickupable.TryAbsorb))]
        public static class PickupableTryAbsorbPatch
        {
            public static bool Prefix(Pickupable __instance, Pickupable other, bool allow_cross_storage, ref bool __result)
            {
                if (!MultiplayerSession.IsClient || !MultiplayerSession.InActiveSession)
                    return true;
                if (allow_cross_storage || other == null)
                    return true;
                if (__instance.storage != null || other.storage != null)
                    return true;

                __result = false;
                return false;
            }
        }

        [HarmonyPatch(typeof(Pickupable), nameof(Pickupable.OnCleanUp))]
        public static class PickupableCleanedUpPatch
        {
            private static long _skipCount;

            public static void Postfix(Pickupable __instance)
            {
                using var _ = Profiler.Scope();
                try
                {
                    if (!IsGroundItemCandidate(__instance))
                        return;

                    if (!MultiplayerSession.IsHost || !MultiplayerSession.InActiveSession)
                        return;

                    // Absorbed into another stack: PickupableAbsorbPatch has told the
                    // clients, with the new amount of the absorber.
                    if (__instance.wasAbsorbed)
                        return;

                    var identity = __instance.GetNetIdentity();
                    if (identity == null || identity.NetId == 0)
                    {
                        _skipCount++;
                        return;
                    }

                    // Bug-D: log every 100 skips so we don't spam the console but still detect persistent unnetworked pickups
                    if (_skipCount > 0 && _skipCount % 100 == 0)
                    {
                        DebugConsole.LogWarning($"[PickupablePatches] Skipped {_skipCount} cleanup sync events for unnetworked pickups (harmless unless items desync)");
                    }

                    PacketSender.SendToAllClients(new GroundItemPickedUpPacket { NetId = identity.NetId });
                }
                catch (System.Exception ex)
                {
                    DebugConsole.LogError($"[PickupableCleanedUpPatch] Exception: {ex}");
                }
            }
        }
    }
}
