using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using ONI_Together.DebugTools;
using ONI_Together.Misc;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.OxySync.Packets;
using Shared.Helpers;
using Shared.OxySync;
using Shared.OxySync.Attributes;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.OxySync.Components
{
    public class OxySyncManager : MonoBehaviour
    {
        public static OxySyncManager? Instance { get; private set; }

        private readonly List<NetworkBehaviour> _behaviours = new();
        private readonly Dictionary<(int Group, PacketSendMode Mode), List<(int Hash, Variant Value)>> _changedByGroup = new();
        private readonly HashSet<Type> _explicitGroupTypes = new();
        private readonly Dictionary<int, HashSet<NetworkBehaviour>> _behavioursByGroup = new();

        private readonly Dictionary<(int, int), NetworkBehaviour> _behaviourLookup = new();
        private readonly Dictionary<(int NetId, int TypeHash), int> _typeOrdinals = new();

        private sealed class PendingSnapshot
        {
            public object Connection;
            public Queue<NetworkBehaviour> Behaviours;
        }

        private readonly Dictionary<(ulong Player, int Group), PendingSnapshot> _pendingSnapshots = new();
        private readonly List<(ulong Player, int Group)> _snapshotKeys = new();
        private readonly Dictionary<ulong, int> _snapshotBudgets = new();
        private float _nextSnapshotTick;
        private const int SnapshotBudgetPerPlayer = 16;

        private float _tickAccumulator;

        public int RegisteredCount => _behaviours.Count;
        public IReadOnlyList<NetworkBehaviour> AllBehaviours => _behaviours;

        public static bool TryGetBehaviour(int NetId, int BehaviourId, out NetworkBehaviour behaviour)
        {
            if (Instance == null)
            {
                behaviour = null;
                return false;
            }

            return Instance._behaviourLookup.TryGetValue((NetId, BehaviourId), out behaviour);
        }

        private void Awake()
        {
            Instance = this;

            NetworkBehaviour.OnSpawned += Register;
            NetworkBehaviour.OnBehaviourCleanUp += Unregister;

            NetworkBehaviour.NetIdQuery = (behaviour) => behaviour.GetComponent<NetworkIdentity>()?.NetId ?? 0;

            NetworkBehaviour.NetIdSetter = (behaviour, newNetId) => behaviour.gameObject.AddOrGet<NetworkIdentity>().OverrideNetId(newNetId);

            NetIdentityHelper.SetIdentity = (go, netId) =>
            {
                var identity = go.AddOrGet<NetworkIdentity>();
                if (netId != 0)
                    identity.OverrideNetId(netId);
                else if (identity.NetId == 0)
                    identity.RegisterIdentity();
                return identity.NetId;
            };

            NetIdentityHelper.OverrideIdentity = (go, netId) =>
            {
                var identity = go.AddOrGet<NetworkIdentity>();
                identity.OverrideNetId(netId);
                return identity.NetId;
            };

            NetworkBehaviour.LogWarning = (msg) => DebugConsole.LogWarning(msg);

            NetworkBehaviour.IsHostQuery = () => MultiplayerSession.IsHost;
            NetworkBehaviour.IsClientQuery = () => MultiplayerSession.IsClient;
            NetworkBehaviour.InSessionQuery = () => MultiplayerSession.InActiveSession;

            NetworkBehaviour.SendCommandToHost = (netId, behaviourId, methodHash, args, sendType) =>
            {
                PacketSender.SendToHost(new CommandPacket
                {
                    NetId = netId,
                    BehaviourId = behaviourId,
                    MethodHash = methodHash,
                    Args = args,
                }, (PacketSendMode)sendType);
                return true;
            };

            NetworkBehaviour.SendClientRpcToAll = (netId, behaviourId, methodHash, args, sendType) =>
            {
                PacketSender.SendToAllClients(new ClientRpcPacket
                {
                    NetId = netId,
                    BehaviourId = behaviourId,
                    MethodHash = methodHash,
                    Args = args,
                    TargetPlayerId = ulong.MaxValue,
                }, (PacketSendMode)sendType);
                return true;
            };

            NetworkBehaviour.SendClientRpcToGroup = (group, netId, behaviourId, methodHash, args, sendType) =>
            {
                PacketSender.SendToGroup(group, new ClientRpcPacket
                {
                    NetId = netId,
                    BehaviourId = behaviourId,
                    MethodHash = methodHash,
                    Args = args,
                    TargetPlayerId = ulong.MaxValue,
                }, (PacketSendMode)sendType);
                return true;
            };

            NetworkBehaviour.LocalUserIdQuery = () => MultiplayerSession.LocalUserID;

            NetworkBehaviour.SendTargetRpcToPlayer = (targetPlayer, netId, behaviourId, methodHash, args, sendType) =>
            {
                PacketSender.SendToPlayer(targetPlayer, new ClientRpcPacket
                {
                    NetId = netId,
                    BehaviourId = behaviourId,
                    MethodHash = methodHash,
                    Args = args,
                    TargetPlayerId = targetPlayer,
                }, (PacketSendMode)sendType);
                return true;
            };
        }

        private void OnDestroy()
        {
            NetworkBehaviour.OnSpawned -= Register;
            NetworkBehaviour.OnBehaviourCleanUp -= Unregister;

            if (Instance == this)
                Instance = null;
        }

		private void Register(NetworkBehaviour behaviour)
		{
			if (!_behaviours.Contains(behaviour))
				_behaviours.Add(behaviour);

            int netId = behaviour.NetId;
            // Components can spawn before their saved identity owns a registry entry;
            // RegisterIdentity will index them once their real address is available.
            if (netId != 0 && NetworkIdentityRegistry.TryGet(netId, out var owner, logFailure: false) &&
                owner.gameObject == behaviour.gameObject)
            {
                ResolveBehaviourId(behaviour, netId);
                behaviour.RegisteredNetId = netId;
                _behaviourLookup[(netId, behaviour.BehaviourId)] = behaviour;
            }

			if (behaviour.GetType().GetCustomAttribute<FixedInterestGroupAttribute>() != null)
				_explicitGroupTypes.Add(behaviour.GetType());

			if (behaviour.InterestGroup == -1 && !_explicitGroupTypes.Contains(behaviour.GetType()))
			{
				int worldId = behaviour.GetMyWorldId();
				if (worldId >= 0)
					behaviour.InterestGroup = WorldChunkHelper.GetGroupId(worldId,
						Grid.PosToCell(behaviour.transform.position));
			}

			IndexBehaviour(behaviour);
		}

        private void Unregister(NetworkBehaviour behaviour)
        {
            _behaviours.Remove(behaviour);

            // Remove by the key it was filed under. The live NetId is not reliable here:
            // the NetworkIdentity next to it may already be destroyed, or overridden since
            // registration. Only an entry that is actually this behaviour is dropped.
            RemoveLookupEntry((behaviour.RegisteredNetId, behaviour.BehaviourId), behaviour);
            RemoveLookupEntry((behaviour.NetId, behaviour.BehaviourId), behaviour);

            RemoveBehaviourFromGroupIndex(behaviour, behaviour.InterestGroup);
            var fields = behaviour.SyncVarFields;
            for (int i = 0; i < fields.Count; i++)
            {
                int g = fields[i].InterestGroup;
                if (g != -1)
                    RemoveBehaviourFromGroupIndex(behaviour, g);
            }
        }

        private void Update()
        {
            if (!MultiplayerSession.IsHost) return;
            PumpPendingSnapshots();
            if (_behaviours.Count == 0) return;

            _tickAccumulator += Time.unscaledDeltaTime;
            _tickAccumulator = Mathf.Min(_tickAccumulator, GameServer.TickInterval * GameServer.MaxMissedTicks);
            if (_tickAccumulator < GameServer.TickInterval)
                return;
            _tickAccumulator -= GameServer.TickInterval;

            var sw = Stopwatch.StartNew();
            int totalChanges = 0;

            for (int i = _behaviours.Count - 1; i >= 0; i--)
            {
                var behaviour = _behaviours[i];
                if (behaviour.IsNullOrDestroyed())
                {
                    _behaviours.RemoveAt(i);
                    RemoveLookupEntry((behaviour.RegisteredNetId, behaviour.BehaviourId), behaviour);
                    continue;
                }

                if (Time.unscaledTime - behaviour._lastSyncTime < behaviour.SyncInterval)
                    continue;

                behaviour._lastSyncTime = Time.unscaledTime;

                ulong manualDirty = behaviour.GetAndClearDirtyBits();

                _changedByGroup.Clear();
                CollectChanges(behaviour, manualDirty, _changedByGroup);

                if (_changedByGroup.Count == 0) continue;

                var identity = behaviour.GetComponent<NetworkIdentity>();
                if (identity == null || identity.NetId == 0)
                    continue;

                int netId = identity.NetId;
                int behaviourId = behaviour.BehaviourId;
                long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                foreach (var kvp in _changedByGroup)
                {
                    int groupId = kvp.Key.Group;
                    var sendMode = kvp.Key.Mode;
                    var updates = kvp.Value;
                    totalChanges += updates.Count;
                    ONI_Together.Networking.Transport.NetStats.RecordSyncFields(behaviour.GetType().Name, updates.Count, snapshot: false);

                    if (updates.Count == 1)
                    {
                        var update = updates[0];
                        PacketSender.SendToGroup(groupId, new SyncVarPacket
                        {
                            NetId = netId,
                            BehaviourId = behaviourId,
                            FieldHash = update.Hash,
                            Value = update.Value,
                            Timestamp = timestamp,
                        }, sendMode);
                    }
                    else
                    {
						var batch = new SyncVarBatchPacket(netId, behaviourId, updates)
                        {
                            Timestamp = timestamp,
                        };
                        PacketSender.SendToGroup(groupId, batch, sendMode);
                    }
                }

                bool hasSubscribers = false;
                foreach (var key in _changedByGroup.Keys)
                {
                    if (InterestGroupManager.GetPlayersInGroup(key.Group).Count > 0)
                    {
                        hasSubscribers = true;
                        break;
                    }
                }

                if (hasSubscribers)
                    behaviour._lastActiveSyncTime = Time.unscaledTime;

                behaviour.SyncLastSentValues();

				if (!_explicitGroupTypes.Contains(behaviour.GetType()))
				{
					int currentWorld = behaviour.GetMyWorldId();
					if (currentWorld >= 0)
					{
						int newGroup = WorldChunkHelper.GetGroupId(currentWorld, Grid.PosToCell(behaviour.transform.position));
						if (newGroup != behaviour.InterestGroup)
                        {
                            RemoveBehaviourFromGroupIndex(behaviour, behaviour.InterestGroup);
                            behaviour.InterestGroup = newGroup;
                            AddBehaviourToGroupIndex(behaviour, newGroup);
                            behaviour.MarkAllDirty(); // Looking at this I'm not 100% sure I need this anymore but I'll leave it - Lyraedan
                        }
					}
				}
            }

            if (totalChanges > 0)
            {
                sw.Stop();
                SyncStats.RecordSync(SyncStats.OxySync, totalChanges, totalChanges * 16, sw.ElapsedMilliseconds);
            }
        }

        internal static void CollectChanges(NetworkBehaviour behaviour, ulong manualDirty, Dictionary<(int Group, PacketSendMode Mode), List<(int Hash, Variant Value)>> changes)
        {
            var fields = behaviour.SyncVarFields;

            ulong remaining = manualDirty;
            while (remaining != 0)
            {
                int index = BitUtils.TrailingZeroCount(remaining);
                remaining &= remaining - 1;

                if (index >= fields.Count) continue;

                var field = fields[index];
                AddChange(changes, behaviour, field, VariantHelper.ObjectToVariant(field.Info.GetValue(behaviour)));
            }

            for (int j = 0; j < fields.Count; j++)
            {
                if ((manualDirty & (1UL << j)) != 0) continue;

                var field = fields[j];
                var currentValue = field.Info.GetValue(behaviour);
                var currentVariant = VariantHelper.ObjectToVariant(currentValue);
                var lastVariant = VariantHelper.ObjectToVariant(field.LastSentValue);
                if (!VariantHelper.ValuesDiffer(currentVariant, lastVariant, field.Epsilon))
                    continue;

                AddChange(changes, behaviour, field, currentVariant);
            }
        }

        private static void AddChange(Dictionary<(int Group, PacketSendMode Mode), List<(int Hash, Variant Value)>> changes, NetworkBehaviour behaviour, NetworkBehaviour.SyncVarField field, Variant value)
        {
            int group = field.InterestGroup;
            if (group == -1) group = behaviour.InterestGroup;
            var key = (group, (PacketSendMode)field.SendMode);
            if (!changes.TryGetValue(key, out var list))
            {
                list = new List<(int Hash, Variant Value)>();
                changes[key] = list;
            }
            list.Add((field.Hash, value));
        }

        private void IndexBehaviour(NetworkBehaviour behaviour)
        {
            var fields = behaviour.SyncVarFields;
            var grouped = new HashSet<int>();

            int primaryGroup = behaviour.InterestGroup;
            if (primaryGroup != -1 && grouped.Add(primaryGroup))
                AddBehaviourToGroupIndex(behaviour, primaryGroup);

            for (int i = 0; i < fields.Count; i++)
            {
                int g = fields[i].InterestGroup;
                if (g == -1) continue;
                if (grouped.Add(g))
                    AddBehaviourToGroupIndex(behaviour, g);
            }
        }

        private void AddBehaviourToGroupIndex(NetworkBehaviour behaviour, int groupId)
        {
            if (!_behavioursByGroup.TryGetValue(groupId, out var set))
            {
                set = new HashSet<NetworkBehaviour>();
                _behavioursByGroup[groupId] = set;
            }
            set.Add(behaviour);
        }

        private void RemoveBehaviourFromGroupIndex(NetworkBehaviour behaviour, int groupId)
        {
            if (_behavioursByGroup.TryGetValue(groupId, out var set))
            {
                set.Remove(behaviour);
                if (set.Count == 0)
                    _behavioursByGroup.Remove(groupId);
            }
        }

        public static void SendFullStateToPlayerForGroup(ulong playerId, int groupId)
        {
            if (Instance == null) return;
            if (!MultiplayerSession.IsHost) return;

            // A cursor packet can trail its sender's disconnect by a frame; without this every
            // behaviour in the group tried to send and logged "no connection" (1740 lines in one frame).
            if (!MultiplayerSession.ConnectedPlayers.TryGetValue(playerId, out var player) || player.Connection == null) return;

            if (!Instance._behavioursByGroup.TryGetValue(groupId, out var behavioursInGroup))
                return;

            var key = (playerId, groupId);
            if (Instance._pendingSnapshots.TryGetValue(key, out var pending)
                && ONI_Together.Networking.Transport.ConnectionIdentityComparer.Instance.Equals(pending.Connection, player.Connection))
                return;

            Instance._pendingSnapshots[key] = new PendingSnapshot
            {
                Connection = player.Connection,
                Behaviours = new Queue<NetworkBehaviour>(behavioursInGroup)
            };
        }

        private void PumpPendingSnapshots()
        {
            if (_pendingSnapshots.Count == 0 || Time.unscaledTime < _nextSnapshotTick) return;
            _nextSnapshotTick = Time.unscaledTime + 0.1f;
            _snapshotBudgets.Clear();
            _snapshotKeys.Clear();
            _snapshotKeys.AddRange(_pendingSnapshots.Keys);
            foreach (var key in _snapshotKeys)
            {
                var pending = _pendingSnapshots[key];
                if (!MultiplayerSession.ConnectedPlayers.TryGetValue(key.Player, out var player)
                    || player.Connection == null
                    || !ONI_Together.Networking.Transport.ConnectionIdentityComparer.Instance.Equals(pending.Connection, player.Connection)
                    || !InterestGroupManager.IsPlayerInGroup(key.Player, key.Group))
                {
                    _pendingSnapshots.Remove(key);
                    continue;
                }

                _snapshotBudgets.TryGetValue(key.Player, out int used);
                while (pending.Behaviours.Count > 0 && used < SnapshotBudgetPerPlayer
                    && NetworkConfig.TransportPacketSender.CanSendSnapshot(player.Connection))
                {
                    var behaviour = pending.Behaviours.Dequeue();
                    used++;
                    if (!behaviour.IsNullOrDestroyed())
                        SendBehaviourSnapshot(key.Player, key.Group, behaviour);
                }
                _snapshotBudgets[key.Player] = used;
                if (pending.Behaviours.Count == 0) _pendingSnapshots.Remove(key);
            }
        }

        private static void SendBehaviourSnapshot(ulong playerId, int groupId, NetworkBehaviour behaviour)
        {

                int netId = behaviour.NetId;
                if (netId == 0) return;
                int behaviourId = behaviour.BehaviourId;

                var fields = behaviour.SyncVarFields;
                if (fields.Count == 0) return;

                var updates = new List<(int Hash, Variant Value)>();
                for (int i = 0; i < fields.Count; i++)
                {
                    var field = fields[i];
                    int fieldGroup = field.InterestGroup;
                    if (fieldGroup == -1) fieldGroup = behaviour.InterestGroup;
                    if (fieldGroup != groupId) continue;

                    updates.Add((field.Hash, VariantHelper.ObjectToVariant(field.Info.GetValue(behaviour))));
                }

                if (updates.Count == 0) return;

                ONI_Together.Networking.Transport.NetStats.RecordSyncFields(behaviour.GetType().Name, updates.Count, snapshot: true);
                long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                if (updates.Count == 1)
                {
                    var update = updates[0];
                    PacketSender.SendToPlayer(playerId, new SyncVarPacket
                    {
                        NetId = netId,
                        BehaviourId = behaviourId,
                        FieldHash = update.Hash,
                        Value = update.Value,
                        Timestamp = timestamp,
                    }, PacketSendMode.ReliableImmediate);
                }
                else
                {
					PacketSender.SendToPlayer(playerId, new SyncVarBatchPacket(netId, behaviourId, updates)
                    {
                        Timestamp = timestamp,
                    }, PacketSendMode.ReliableImmediate);
                }
        }

        /// <summary>
        /// Two live behaviours of one type on one NetId cannot share a key, so the second
        /// is moved one id over - and the other side, which does not see the collision,
        /// will keep addressing it under the original id. That is a last resort and gets
        /// a warning.
        ///
        /// What must never trigger it is a dead entry. Before this, anything that missed
        /// its unregister - every behaviour of a world torn down by a scene load, see
        /// NetworkBehaviour.OnForcedCleanUp - kept its key, and the same object reloaded
        /// from the save was pushed to id+1 while the host still sent id. A stale entry is
        /// evicted here instead, and so is an entry for this very behaviour registering a
        /// second time.
        /// </summary>
        private void ResolveBehaviourId(NetworkBehaviour behaviour, int netId)
        {
            // A temporary collision at the old NetId must not permanently shift
            // the component's wire type when its identity is assigned/overridden.
            behaviour.BehaviourId = (behaviour.GetType().FullName ?? behaviour.GetType().Name).GetHashCode();
            int id = behaviour.BehaviourId;

            while (_behaviourLookup.TryGetValue((netId, id), out var existing))
            {
                if (ReferenceEquals(existing, behaviour) || existing.IsNullOrDestroyed())
                {
                    _behaviourLookup.Remove((netId, id));
                    break;
                }

                id++;
            }

            if (id != behaviour.BehaviourId)
            {
                DebugConsole.LogWarning($"[OxySync] {behaviour.GetType().Name} on NetId {netId} collides with a live {_behaviourLookup[(netId, behaviour.BehaviourId)].GetType().Name}; filed under {id} instead of {behaviour.BehaviourId}. Packets addressed to the original id will not reach it.");
                behaviour.BehaviourId = id;
            }
        }

        private void RemoveLookupEntry((int, int) key, NetworkBehaviour behaviour)
        {
            if (_behaviourLookup.TryGetValue(key, out var existing) && ReferenceEquals(existing, behaviour))
                _behaviourLookup.Remove(key);
        }

        /// <summary>
        /// Moves every registered behaviour on this object to the NetId it now carries.
        /// NetworkIdentity calls this when its NetId is assigned or overridden after the
        /// behaviours spawned; without it the lookup keeps the old key and packets for the
        /// new one fall through to the component scan in ResolveBehaviour.
        /// </summary>
        public static void RekeyBehaviours(GameObject go, int newNetId)
        {
            if (Instance == null || go == null || newNetId == 0) return;

            foreach (var behaviour in go.GetComponents<NetworkBehaviour>())
            {
                if (behaviour.IsNullOrDestroyed()) continue;
                if (!Instance._behaviours.Contains(behaviour)) continue;

                if (behaviour.RegisteredNetId == newNetId &&
                    Instance._behaviourLookup.TryGetValue((newNetId, behaviour.BehaviourId), out var current) &&
                    ReferenceEquals(current, behaviour))
                    continue;

                Instance.RemoveLookupEntry((behaviour.RegisteredNetId, behaviour.BehaviourId), behaviour);
                Instance.ResolveBehaviourId(behaviour, newNetId);
                behaviour.RegisteredNetId = newNetId;
                Instance._behaviourLookup[(newNetId, behaviour.BehaviourId)] = behaviour;
            }
        }

        /// <summary>
        /// Forgets every behaviour. Called where a world is about to be unloaded: this
        /// object outlives the scene while the behaviours do not, and a stale entry is
        /// worse than a missing one (see ResolveBehaviourId).
        /// </summary>
                public static void ClearAll()
                {
                    _fallbackWarned.Clear();
                    NetworkTransform.ResetHostClock();
                    ONI_Together.Networking.Synchronization.WorldGenSpawnMap.Clear();
                    PendingWorkableProgress.Clear();
                    ONI_Together.Networking.Synchronization.GameClockSync.Reset();

            if (Instance == null) return;
            Instance._behaviours.Clear();
            Instance._behaviourLookup.Clear();
            Instance._behavioursByGroup.Clear();
            Instance._changedByGroup.Clear();
            Instance._pendingSnapshots.Clear();
            Instance._snapshotKeys.Clear();
            Instance._snapshotBudgets.Clear();
            Instance._nextSnapshotTick = 0f;
            Instance._typeOrdinals.Clear();
        }

        private static readonly HashSet<(int, int)> _fallbackWarned = new();

        /// <summary>
        /// The behaviour a packet is addressed to, or null.
        ///
        /// The lookup is the fast path. When it misses - or holds a destroyed behaviour -
        /// the object behind the NetId is scanned for a behaviour with the requested id,
        /// and that one is re-indexed so the next packet hits the fast path again. The
        /// old fallback took the first NetworkBehaviour on the object whatever its type;
        /// on a duplicant that is AnimSyncer, which has no sync vars, so position updates
        /// addressed to the position handler were dropped without a word. A miss is now
        /// logged once per address.
        /// </summary>
        public static NetworkBehaviour ResolveBehaviour(int netId, int behaviourId)
        {
            if (Instance != null &&
                Instance._behaviourLookup.TryGetValue((netId, behaviourId), out var found) &&
                !found.IsNullOrDestroyed())
                return found;

            if (!NetworkIdentityRegistry.TryGet(netId, out var identity) || identity.IsNullOrDestroyed() || identity.gameObject.IsNullOrDestroyed())
                return null;

            var candidates = identity.gameObject.GetComponents<NetworkBehaviour>();
            NetworkBehaviour match = null;
            foreach (var candidate in candidates)
            {
                if (candidate.IsNullOrDestroyed()) continue;
                if (candidate.BehaviourId == behaviourId)
                {
                    match = candidate;
                    break;
                }
            }

            if (match == null)
            {
                if (_fallbackWarned.Add((netId, behaviourId)))
                {
                    string present = string.Join(", ", candidates.Where(c => !c.IsNullOrDestroyed()).Select(c => $"{c.GetType().Name}={c.BehaviourId}"));
                    DebugConsole.LogWarning($"[OxySync] No behaviour {behaviourId} on NetId {netId} ({identity.gameObject.name}); present: [{present}]. Packet dropped.");
                }
                return null;
            }

            if (Instance != null)
            {
                Instance.RemoveLookupEntry((match.RegisteredNetId, match.BehaviourId), match);
                match.RegisteredNetId = netId;
                Instance._behaviourLookup[(netId, behaviourId)] = match;
                if (!Instance._behaviours.Contains(match))
                    Instance._behaviours.Add(match);
                if (_fallbackWarned.Add((netId, behaviourId)))
                    DebugConsole.Log($"[OxySync] Re-indexed {match.GetType().Name} on NetId {netId} ({identity.gameObject.name}); it was not in the lookup under its id.");
            }

            return match;
        }
    }
}