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
        [HarmonyPatch(typeof(Pickupable), nameof(Pickupable.OnSpawn))]
        public static class PickupableOnSpawnPatch
        {
            public static void Postfix(Pickupable __instance)
            {
                using var _ = Profiler.Scope();
                try
                {
                    if (__instance == null || __instance.gameObject == null)
                        return;

                    // Do not treat living critters, minions, or TargetLocators as loose substance/ore items
                    if (__instance.GetComponent<CreatureBrain>() != null || 
                        __instance.GetComponent<Health>() != null || 
                        __instance.GetComponent<MinionIdentity>() != null ||
                        __instance.name.Contains("TargetLocator"))
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

                    if (identity.NetId == 0)
                        identity.RegisterIdentity();

                    if (identity.NetId == 0)
                        return;

                    // Check if it is a substance resource (ore, dirt, liquid, gas chunk)
                    var pe = __instance.GetComponent<PrimaryElement>();
                    bool isSubstance = pe != null && pe.Element != null && pe.Mass > 0f && 
                                       pe.ElementID != SimHashes.Creature && 
                                       pe.ElementID != SimHashes.Void &&
                                       __instance.GetComponent<SubstanceChunk>() != null;

                    if (isSubstance)
                    {
                        var packet = new SpawnPrefabPacket(
                            identity.NetId,
                            pe.Element.id.GetHashCode(),
                            __instance.transform.position,
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
                        var tag = __instance.PrefabID();
                        var packet = new SpawnPrefabPacket(
                            identity.NetId,
                            tag.GetHashCode(),
                            __instance.transform.position,
                            tag.Name
                        )
                        {
                            IsActive = __instance.gameObject.activeSelf
                        };
                        PacketSender.SendToAllClients(packet);
                    }
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


        [HarmonyPatch(typeof(Pickupable), nameof(Pickupable.OnCleanUp))]
        public static class PickupableCleanedUpPatch
        {
            private static long _skipCount;

            public static void Postfix(Pickupable __instance)
            {
                using var _ = Profiler.Scope();
                try
                {
                    if (__instance == null || __instance.gameObject == null)
                        return;

                    // Do not treat critters, minions, or plants as ground pickup items
                    if (__instance.GetComponent<CreatureBrain>() != null || 
                        __instance.GetComponent<Health>() != null || 
                        __instance.GetComponent<MinionIdentity>() != null ||
                        __instance.name.Contains("TargetLocator"))
                        return;

                    if (!MultiplayerSession.IsHost || !MultiplayerSession.InActiveSession)
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
