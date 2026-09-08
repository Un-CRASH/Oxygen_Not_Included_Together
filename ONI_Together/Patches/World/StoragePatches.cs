using System.Collections.Generic;
using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.DuplicantActions;
using ONI_Together.Networking.Packets.World;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Patches.World
{
	public static class StoragePatches
	{
        [HarmonyPatch(typeof(Storage), nameof(Storage.Remove))]
        public static class StorageRemovePatch
        {
            public static void Postfix(Storage __instance, GameObject go)
            {
                using var _ = Profiler.Scope();

                if (!MultiplayerSession.IsHost || !MultiplayerSession.InActiveSession) return;
                if (__instance == null || go == null) return;

                var storageIdentity = __instance.GetNetIdentity();
                if (storageIdentity == null || storageIdentity.NetId == 0) return;
                
                // If this is a duplicant's storage, notify clients to remove the carried item visual
                if (__instance.GetComponent<MinionBrain>() != null)
                {
                    var goIdentity = go.GetNetIdentity();
                    int removedItemNetId = (goIdentity != null) ? goIdentity.NetId : 0;
                    PacketSender.SendToAllClients(new DuplicantCarryItemPacket
                    {
                        NetId = storageIdentity.NetId,
                        PickupableNetId = removedItemNetId,
                        IsCarrying = false
                    });
                }
                else
                {
                    var pe = go.GetComponent<PrimaryElement>();
                    PacketSender.SendToAllClients(new StorageItemPacket
                    {
                        NetId = 0, // FX Only
                        StorageNetId = storageIdentity.NetId,
                        DoDiseaseTransfer = false,
                        FxPrefix = Storage.FXPrefix.PickedUp,
                        ConsumedPrefabHash = go.PrefabID().GetHashCode(),
                        ConsumedAmount = pe?.Mass ?? 0f
                    });
                }
            }
        }

        // Edible.StopConsuming is called when a dupe finishes eating food.
        // The food GameObject is destroyed directly, bypassing Storage.Remove,
        // so we need a separate patch to clean up the carried item proxy.
        // However, I believe doing it in StartConsuming makes sense since they are removing it from their back
        [HarmonyPatch(typeof(Edible), nameof(Edible.StartConsuming))]
        public static class EdibleStopConsumingPatch
        {
            public static void Postfix(Edible __instance)
            {
                using var _ = Profiler.Scope();

                if (!MultiplayerSession.IsHost || !MultiplayerSession.InActiveSession) return;
                if (__instance == null || __instance.worker == null) return;

                var dupeStorage = __instance.worker.GetComponent<Storage>();
                if (dupeStorage == null) return;

                var storageIdentity = dupeStorage.GetNetIdentity();
                if (storageIdentity == null || storageIdentity.NetId == 0) return;

                var goIdentity = __instance.gameObject.GetNetIdentity();
                int consumedItemNetId = (goIdentity != null) ? goIdentity.NetId : 0;
                PacketSender.SendToAllClients(new DuplicantCarryItemPacket
                {
                    NetId = storageIdentity.NetId,
                    PickupableNetId = consumedItemNetId,
                    IsCarrying = false
                });
            }
        }

        /// <summary>
        /// An item leaving a container for the floor. Nothing announced it before: the
        /// spawn patch skips items born inside containers (refined metal from a smelter,
        /// the output of a crusher, anything a duplicant puts down), so clients never had
        /// a copy - and Drop calls neither Store nor Remove.
        /// </summary>
        [HarmonyPatch(typeof(Storage), nameof(Storage.Drop), new System.Type[] { typeof(GameObject), typeof(bool) })]
        public static class StorageDropPatch
        {
            public static void Postfix(Storage __instance, GameObject go)
            {
                using var _ = Profiler.Scope();
                try
                {
                    AnnounceDropped(__instance, go);
                }
                catch (System.Exception ex)
                {
                    DebugConsole.LogError($"[StorageDropPatch] Exception: {ex}");
                }
            }
        }

        /// <summary>
        /// DropAll walks the item list itself rather than calling Drop, so the list is
        /// taken before and everything that ended up on the floor is announced after.
        /// </summary>
        [HarmonyPatch(typeof(Storage), nameof(Storage.DropAll), new System.Type[] { typeof(Vector3), typeof(bool), typeof(bool), typeof(Vector3), typeof(bool), typeof(List<GameObject>) })]
        public static class StorageDropAllPatch
        {
            public static void Prefix(Storage __instance, out List<GameObject> __state)
            {
                __state = null;
                if (!MultiplayerSession.IsHost || !MultiplayerSession.InActiveSession) return;
                if (__instance == null || __instance.items == null || __instance.items.Count == 0) return;
                __state = new List<GameObject>(__instance.items);
            }

            public static void Postfix(Storage __instance, List<GameObject> __state)
            {
                using var _ = Profiler.Scope();
                if (__state == null) return;
                try
                {
                    foreach (var go in __state)
                        AnnounceDropped(__instance, go);
                }
                catch (System.Exception ex)
                {
                    DebugConsole.LogError($"[StorageDropAllPatch] Exception: {ex}");
                }
            }
        }

        private static void AnnounceDropped(Storage storage, GameObject go)
        {
            if (!MultiplayerSession.IsHost || !MultiplayerSession.InActiveSession) return;
            if (storage == null || go == null || go.IsNullOrDestroyed()) return;
            if (Game.Instance == null || !Game.Instance.isSpawned || GameServerHardSync.IsHardSyncInProgress) return;

            var pickupable = go.GetComponent<Pickupable>();
            if (pickupable == null) return;

            // Still in a container (moved rather than dropped), or vented and dumped away.
            if (pickupable.storage != null) return;

            // A duplicant putting an item down also stops carrying it.
            if (storage.GetComponent<MinionBrain>() != null)
            {
                var storageIdentity = storage.GetExistingNetIdentity();
                if (storageIdentity != null && storageIdentity.NetId != 0)
                {
                    var goIdentity = go.GetExistingNetIdentity();
                    PacketSender.SendToAllClients(new DuplicantCarryItemPacket
                    {
                        NetId = storageIdentity.NetId,
                        PickupableNetId = goIdentity != null ? goIdentity.NetId : 0,
                        IsCarrying = false
                    });
                }
            }

            PickupablePatches.AnnounceToClients(pickupable);
        }

        // Pickupable.OnCleanUp only fires when the object is destroyed. Items that are
        // reparented into Storage (seeds into planters, eggs into incubators, live
        // critters, non-stackable items) stay alive and never trigger OnCleanUp, so
        // clients keep rendering them on the ground.
        [HarmonyPatch(typeof(Storage), nameof(Storage.Store), new System.Type[] { typeof(GameObject), typeof(bool), typeof(bool), typeof(bool), typeof(bool) })]
        public static class StorageStorePatch
        {
            /// <summary>Whether the item was in some storage (a duplicant's hands, another container) before this call.</summary>
            public static void Prefix(GameObject go, out bool __state)
            {
                __state = go != null && go.TryGetComponent<Pickupable>(out var pickupable) && pickupable.storage != null;
            }

            public static void Postfix(Storage __instance, GameObject go, bool hide_popups, bool block_events, bool do_disease_transfer, bool is_deserializing, bool __state)
            {
                using var _ = Profiler.Scope();
                try
                {
                    if (!MultiplayerSession.IsHost || !MultiplayerSession.InActiveSession)
                        return;
                    if (go == null || is_deserializing)
                        return;

                    var storageIdentity = __instance.GetNetIdentity();
                    if (storageIdentity == null || storageIdentity.NetId == 0)
                        return;

                    var identity = go.GetNetIdentity();
                    var pe = go.GetComponent<PrimaryElement>();
                    int itemNetId = (identity != null) ? identity.NetId : 0;
                    
                    // If this is a duplicant's storage, notify clients to show the item on their back
                    // Works even for container-origin items that may lack a NetworkIdentity
                    if (__instance.GetComponent<MinionBrain>() != null)
                    {
                        var itemAnimCtrl = go.GetComponentInChildren<KBatchedAnimController>();
                        var animFile = itemAnimCtrl?.AnimFiles?[0]?.name;
                        if (animFile != null)
                        {
                            PacketSender.SendToAllClients(new DuplicantCarryItemPacket
                            {
                                NetId = storageIdentity.NetId,
                                PickupableNetId = itemNetId,
                                AnimFileName = animFile,
                                ItemPrefabHash = go.PrefabID().GetHashCode(),
                                IsCarrying = true
                            });
                        }
                    }
                    else
                    {
                        // An item that came out of a duplicant's hands or another container
                        // has no copy on the clients under this id (the ground copy went with
                        // the pickup, container contents are rebuilt from the blob), so only
                        // the delivery FX is worth sending. Addressed by id, every one of those
                        // was a miss that sat in the pending set for the rest of the session.
                        PacketSender.SendToAllClients(new StorageItemPacket
                        {
                            NetId = __state ? 0 : itemNetId,
                            StorageNetId = storageIdentity.NetId,
                            DoDiseaseTransfer = do_disease_transfer,
                            FxPrefix = Storage.FXPrefix.Delivered,
                            ConsumedPrefabHash = go.PrefabID().GetHashCode(),
                            ConsumedAmount = pe?.Mass ?? 0
                        });
                    }
                }
                catch (System.Exception ex)
                {
                    DebugConsole.LogError($"[StorageStorePatch] Exception: {ex}");
                }
            }
        }
    }
}
