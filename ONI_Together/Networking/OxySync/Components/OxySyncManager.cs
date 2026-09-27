using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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

        private readonly List<ISyncBehaviour> _behaviours = new();
        private readonly Dictionary<(int Group, PacketSendMode Mode), List<(int Hash, Variant Value)>> _changedByGroup = new();
        private readonly HashSet<Type> _explicitGroupTypes = new();
        private readonly Dictionary<int, HashSet<ISyncBehaviour>> _behavioursByGroup = new();

        private readonly Dictionary<(int, int), ISyncBehaviour> _behaviourLookup = new();
        private readonly Dictionary<(int NetId, int TypeHash), int> _typeOrdinals = new();

        /// <summary>
        /// One adapter per native behaviour. NativeSyncBehaviour has reference identity, so a
        /// second wrapper around the same component would be a different entry in the list,
        /// the group index and the lookup; everything here compares behaviours by reference.
        /// Keyed by reference too: a destroyed UnityEngine.Object compares equal to null.
        /// </summary>
        private readonly Dictionary<NetworkBehaviour, NativeSyncBehaviour> _nativeAdapters = new(ReferenceComparer.Instance);

        private sealed class ReferenceComparer : IEqualityComparer<NetworkBehaviour>
        {
            public static readonly ReferenceComparer Instance = new();
            public bool Equals(NetworkBehaviour x, NetworkBehaviour y) => ReferenceEquals(x, y);
            public int GetHashCode(NetworkBehaviour obj) => RuntimeHelpers.GetHashCode(obj);
        }

        private sealed class PendingSnapshot
        {
            public object Connection;
            public Queue<ISyncBehaviour> Behaviours;
        }

        private readonly Dictionary<(ulong Player, int Group), PendingSnapshot> _pendingSnapshots = new();
        private readonly List<(ulong Player, int Group)> _snapshotKeys = new();
        private readonly Dictionary<ulong, int> _snapshotBudgets = new();
        private float _nextSnapshotTick;
        private const int SnapshotBudgetPerPlayer = 16;

        private float _tickAccumulator;

        public int RegisteredCount => _behaviours.Count;
        public IReadOnlyList<ISyncBehaviour> AllBehaviours => _behaviours;

        /// <summary>
        /// Native (ONI Together) behaviours only, for tooling that needs the typed
        /// <see cref="Shared.OxySync.NetworkBehaviour"/> surface (e.g. the debug inspector).
        /// </summary>
        public IReadOnlyList<NetworkBehaviour> NativeBehaviours
        {
            get
            {
                var result = new List<NetworkBehaviour>(_behaviours.Count);
                for (int i = 0; i < _behaviours.Count; i++)
                {
                    if (_behaviours[i] is NativeSyncBehaviour native && !native.IsDestroyed)
                        result.Add(native.Native);
                }
                return result;
            }
        }

        /// <summary>
        /// Behaviours that came from the ONI_Together_API assembly, exposed for the debug inspector.
        /// </summary>
        public IReadOnlyList<ForeignSyncBehaviour> ForeignBehaviours
        {
            get
            {
                var result = new List<ForeignSyncBehaviour>(_behaviours.Count);
                for (int i = 0; i < _behaviours.Count; i++)
                {
                    if (_behaviours[i] is ForeignSyncBehaviour foreign && !foreign.IsDestroyed)
                        result.Add(foreign);
                }
                return result;
            }
        }

        /// <summary>
        /// Adds or fetches a <see cref="NetworkIdentity"/> on a GameObject and resolves its NetId,
        /// preferring a supplied override. Shared by the native OxySync path and the API bridge.
        /// </summary>
        public static int SetOrGetIdentity(GameObject go, int netId)
        {
            var identity = go.AddOrGet<NetworkIdentity>();
            if (netId != 0)
                identity.OverrideNetId(netId);
            else if (identity.NetId == 0)
                identity.RegisterIdentity();
            return identity.NetId;
        }

        /// <summary>
        /// Adds a <see cref="NetworkIdentity"/> to a GameObject and registers it if it has no NetId,
        /// returning the resolved NetId. Equivalent to <see cref="SetOrGetIdentity"/> with no override.
        /// </summary>
        public static int AddIdentity(GameObject go)
        {
            var identity = go.AddOrGet<NetworkIdentity>();
            if (identity.NetId == 0)
                identity.RegisterIdentity();
            return identity.NetId;
        }

        /// <summary>
        /// Reads the NetId of an existing <see cref="NetworkIdentity"/> on a GameObject without
        /// creating or registering one. Returns 0 when none exists.
        /// </summary>
        public static int GetIdentity(GameObject go)
        {
            if (go == null)
                return 0;

            return go.TryGetComponent<NetworkIdentity>(out var identity) && identity != null
                ? identity.NetId
                : 0;
        }

        /// <summary>
        /// Forces a <see cref="NetworkIdentity"/> on a GameObject to a specific NetId.
        /// </summary>
        public static int OverrideIdentity(GameObject go, int netId)
        {
            var identity = go.AddOrGet<NetworkIdentity>();
            identity.OverrideNetId(netId);
            return identity.NetId;
        }

        public static bool TryGetBehaviour(int NetId, int BehaviourId, out NetworkBehaviour behaviour)
        {
            behaviour = null;
            if (Instance == null)
                return false;

            if (Instance._behaviourLookup.TryGetValue((NetId, BehaviourId), out var syncBehaviour)
                && syncBehaviour is NativeSyncBehaviour native)
            {
                behaviour = native.Native;
                return true;
            }
            return false;
        }

        public static bool TryGetSyncBehaviour(int NetId, int BehaviourId, out ISyncBehaviour behaviour)
        {
            if (Instance == null)
            {
                behaviour = null;
                return false;
            }

            return Instance._behaviourLookup.TryGetValue((NetId, BehaviourId), out behaviour);
        }

        private NativeSyncBehaviour AdapterFor(NetworkBehaviour behaviour)
        {
            if (!_nativeAdapters.TryGetValue(behaviour, out var adapter))
            {
                adapter = new NativeSyncBehaviour(behaviour);
                _nativeAdapters[behaviour] = adapter;
            }
            return adapter;
        }

        /// <summary>The NetId a behaviour was filed under; native behaviours remember it, foreign ones only have their live one.</summary>
        private static int RegisteredNetIdOf(ISyncBehaviour behaviour)
            // A plain field: still readable on a destroyed component, which is when Unregister needs it.
            => behaviour is NativeSyncBehaviour native && (object)native.Native != null ? native.Native.RegisteredNetId : behaviour.NetId;

        private void Awake()
        {
            Instance = this;

            NetworkBehaviour.OnSpawned += Register;
            NetworkBehaviour.OnBehaviourCleanUp += Unregister;

            NetworkBehaviour.NetIdQuery = (behaviour) => behaviour.GetComponent<NetworkIdentity>()?.NetId ?? 0;

            NetworkBehaviour.NetIdSetter = (behaviour, newNetId) => behaviour.gameObject.AddOrGet<NetworkIdentity>().OverrideNetId(newNetId);

            NetIdentityHelper.SetIdentity = SetOrGetIdentity;
            NetIdentityHelper.AddIdentity = AddIdentity;
            NetIdentityHelper.GetIdentity = GetIdentity;
            NetIdentityHelper.OverrideIdentity = OverrideIdentity;

            NetworkBehaviour.LogWarning = (msg) => DebugConsole.LogWarning(msg);

            NetworkBehaviour.IsHostQuery = () => MultiplayerSession.IsHost;
            NetworkBehaviour.IsClientQuery = () => MultiplayerSession.IsClient;
            NetworkBehaviour.InSessionQuery = () => MultiplayerSession.InActiveSession;

            NetworkBehaviour.SendCommandToHost = (netId, behaviourId, methodHash, args, sendType) =>
            {
                // Not registered yet: the host cannot resolve id 0, and the behaviour is
                // rekeyed and resends its state once the identity registers.
                if (netId == 0)
                {
                    DebugConsole.LogAggregated("OxySync.CommandNoId", $"[OxySync] Command {methodHash} for behaviour {behaviourId} dropped: identity not registered yet");
                    return true;
                }
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
            if (behaviour == null) return;
            RegisterSyncBehaviour(AdapterFor(behaviour));
        }

        /// <summary>
        /// Registers an already-adapted behaviour. Used by the native path (via <see cref="Register"/>)
        /// and by <c>OxySync_API_Helper</c> for behaviours loaded from the ONI Together API assembly.
        /// </summary>
        public void RegisterSyncBehaviour(ISyncBehaviour behaviour)
        {
            if (behaviour == null) return;

            if (!_behaviours.Contains(behaviour))
                _behaviours.Add(behaviour);

            behaviour.RefreshSyncVars();

            int netId = behaviour.NetId;
            if (behaviour is NativeSyncBehaviour native)
            {
                // Components can spawn before their saved identity owns a registry entry;
                // RegisterIdentity will index them once their real address is available.
                if (netId != 0 && NetworkIdentityRegistry.TryGet(netId, out var owner, logFailure: false) &&
                    owner.gameObject == behaviour.GameObject)
                {
                    ResolveBehaviourId(behaviour, netId);
                    native.Native.RegisteredNetId = netId;
                    _behaviourLookup[(netId, behaviour.BehaviourId)] = behaviour;
                }
            }
            else if (netId != 0)
            {
                ResolveBehaviourId(behaviour, netId);
                _behaviourLookup[(netId, behaviour.BehaviourId)] = behaviour;
            }

            var behaviourType = behaviour.UnderlyingType;
            if (behaviourType.GetCustomAttribute<FixedInterestGroupAttribute>() != null)
                _explicitGroupTypes.Add(behaviourType);

            if (behaviour.InterestGroup == -1 && !_explicitGroupTypes.Contains(behaviourType))
            {
                int worldId = behaviour.GetMyWorldId();
                if (worldId >= 0 && behaviour.GameObject != null)
                    behaviour.InterestGroup = WorldChunkHelper.GetGroupId(worldId,
                        Grid.PosToCell(behaviour.GameObject.transform.position));
            }

            IndexBehaviour(behaviour);
        }

        private void Unregister(NetworkBehaviour behaviour)
        {
            if (behaviour == null) return;
            if (!_nativeAdapters.TryGetValue(behaviour, out var adapter)) return;

            UnregisterSyncBehaviour(adapter);
            _nativeAdapters.Remove(behaviour);
        }

        /// <summary>
        /// Unregisters an adapted behaviour. Used by the native path and the API bridge.
        /// </summary>
        public void UnregisterSyncBehaviour(ISyncBehaviour behaviour)
        {
            if (behaviour == null) return;

            _behaviours.Remove(behaviour);

            // Remove by the key it was filed under. The live NetId is not reliable here:
            // the NetworkIdentity next to it may already be destroyed, or overridden since
            // registration. Only an entry that is actually this behaviour is dropped.
            RemoveLookupEntry((RegisteredNetIdOf(behaviour), behaviour.BehaviourId), behaviour);
            RemoveLookupEntry((behaviour.NetId, behaviour.BehaviourId), behaviour);

            RemoveBehaviourFromGroupIndex(behaviour, behaviour.InterestGroup);
            int fieldCount = behaviour.SyncVarCount;
            for (int i = 0; i < fieldCount; i++)
            {
                int g = behaviour.GetSyncVar(i).InterestGroup;
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
                if (behaviour.IsDestroyed)
                {
                    _behaviours.RemoveAt(i);
                    RemoveLookupEntry((RegisteredNetIdOf(behaviour), behaviour.BehaviourId), behaviour);
                    if (behaviour is NativeSyncBehaviour dead)
                        _nativeAdapters.Remove(dead.Native);
                    continue;
                }

                if (Time.unscaledTime - behaviour.LastSyncTime < behaviour.SyncInterval)
                    continue;

                behaviour.LastSyncTime = Time.unscaledTime;
                behaviour.RefreshSyncVars();

                ulong manualDirty = behaviour.GetAndClearDirtyBits();

                // Re-home the behaviour in the chunk it is in now, whether or not any
                // SyncVar changed: a behaviour without SyncVars (AnimSyncer on every
                // duplicant) never reached the re-index below, so its RPCs stayed
                // addressed to the chunk it spawned in for the whole session.
                ReindexInterestGroup(behaviour);

                _changedByGroup.Clear();
                CollectChanges(behaviour, manualDirty, _changedByGroup);

                if (_changedByGroup.Count == 0) continue;

                var identity = behaviour.GameObject != null
                    ? behaviour.GameObject.GetComponent<NetworkIdentity>()
                    : null;
                if (identity == null || identity.NetId == 0)
                    continue;

                // A client whose reliable channel is far behind gets no more deltas piled
                // on top; the values stay unsent (dirty) and go out on a later tick. One
                // session's queue to a client grew to 22 000 packets while this loop kept
                // adding SyncVar deltas at 300-460 a second.
                if (AnyTargetBacklogged(_changedByGroup.Keys))
                {
                    behaviour.MarkAllDirty();
                    ONI_Together.Networking.Transport.NetStats.RecordBacklogSkip();
                    continue;
                }

                int netId = identity.NetId;
                int behaviourId = behaviour.BehaviourId;
                long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                foreach (var kvp in _changedByGroup)
                {
                    int groupId = kvp.Key.Group;
                    var sendMode = kvp.Key.Mode;
                    var updates = kvp.Value;
                    totalChanges += updates.Count;
                    ONI_Together.Networking.Transport.NetStats.RecordSyncFields(behaviour.UnderlyingType.Name, updates.Count, snapshot: false);

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
                    behaviour.LastActiveSyncTime = Time.unscaledTime;

                behaviour.SyncLastSentValues();
            }

            if (totalChanges > 0)
            {
                sw.Stop();
                SyncStats.RecordSync(SyncStats.OxySync, totalChanges, totalChanges * 16, sw.ElapsedMilliseconds);
            }
        }

        private static bool AnyTargetBacklogged(IEnumerable<(int Group, PacketSendMode Mode)> keys)
        {
            var sender = NetworkConfig.TransportPacketSender;
            foreach (var key in keys)
            {
                foreach (var playerId in InterestGroupManager.GetGroupMemberIds(key.Group))
                {
                    if (playerId == MultiplayerSession.HostUserID) continue;
                    if (!MultiplayerSession.ConnectedPlayers.TryGetValue(playerId, out var player) || player.Connection == null) continue;
                    if (sender.IsBacklogged(player.Connection))
                        return true;
                }
            }
            return false;
        }

        private void ReindexInterestGroup(ISyncBehaviour behaviour)
        {
            if (_explicitGroupTypes.Contains(behaviour.UnderlyingType))
                return;
            int currentWorld = behaviour.GetMyWorldId();
            if (currentWorld < 0 || behaviour.GameObject == null)
                return;
            int newGroup = WorldChunkHelper.GetGroupId(currentWorld, Grid.PosToCell(behaviour.GameObject.transform.position));
            if (newGroup == behaviour.InterestGroup)
                return;
            int oldGroup = behaviour.InterestGroup;
            RemoveBehaviourFromGroupIndex(behaviour, oldGroup);
            behaviour.InterestGroup = newGroup;
            AddBehaviourToGroupIndex(behaviour, newGroup);
            behaviour.MarkAllDirty(); // the new group gets a full state
            if (behaviour is not NativeSyncBehaviour native)
                return;
            try
            {
                native.Native.OnInterestGroupChanged(oldGroup, newGroup);
            }
            catch (System.Exception ex)
            {
                DebugConsole.LogAggregated("OxySync.GroupChanged", $"[OxySync] {behaviour.UnderlyingType.Name}.OnInterestGroupChanged threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        internal static void CollectChanges(NetworkBehaviour behaviour, ulong manualDirty, Dictionary<(int Group, PacketSendMode Mode), List<(int Hash, Variant Value)>> changes)
        {
            CollectChanges(new NativeSyncBehaviour(behaviour), manualDirty, changes);
        }

        internal static void CollectChanges(ISyncBehaviour behaviour, ulong manualDirty, Dictionary<(int Group, PacketSendMode Mode), List<(int Hash, Variant Value)>> changes)
        {
            int fieldCount = behaviour.SyncVarCount;

            ulong remaining = manualDirty;
            while (remaining != 0)
            {
                int index = BitUtils.TrailingZeroCount(remaining);
                remaining &= remaining - 1;

                if (index >= fieldCount) continue;

                var field = behaviour.GetSyncVar(index);
                AddChange(changes, behaviour, field, VariantHelper.ObjectToVariant(field.GetValue()));
            }

            for (int j = 0; j < fieldCount; j++)
            {
                if ((manualDirty & (1UL << j)) != 0) continue;

                var field = behaviour.GetSyncVar(j);
                var currentValue = field.GetValue();
                var currentVariant = VariantHelper.ObjectToVariant(currentValue);
                var lastVariant = VariantHelper.ObjectToVariant(field.LastSentValue);
                if (!VariantHelper.ValuesDiffer(currentVariant, lastVariant, field.Epsilon))
                    continue;

                AddChange(changes, behaviour, field, currentVariant);
            }
        }

        private static void AddChange(Dictionary<(int Group, PacketSendMode Mode), List<(int Hash, Variant Value)>> changes, ISyncBehaviour behaviour, SyncVarDescriptor field, Variant value)
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

        private void IndexBehaviour(ISyncBehaviour behaviour)
        {
            var grouped = new HashSet<int>();

            int primaryGroup = behaviour.InterestGroup;
            // A fixed-group behaviour (group -1, sent to everyone) is filed under -1 so the
            // player who has just become ready can be sent its state as a snapshot too;
            // until now nothing delivered such state to a joining player until it changed.
            if ((primaryGroup != -1 || _explicitGroupTypes.Contains(behaviour.UnderlyingType)) && grouped.Add(primaryGroup))
                AddBehaviourToGroupIndex(behaviour, primaryGroup);

            int fieldCount = behaviour.SyncVarCount;
            for (int i = 0; i < fieldCount; i++)
            {
                int g = behaviour.GetSyncVar(i).InterestGroup;
                if (g == -1) continue;
                if (grouped.Add(g))
                    AddBehaviourToGroupIndex(behaviour, g);
            }
        }

        private void AddBehaviourToGroupIndex(ISyncBehaviour behaviour, int groupId)
        {
            if (!_behavioursByGroup.TryGetValue(groupId, out var set))
            {
                set = new HashSet<ISyncBehaviour>();
                _behavioursByGroup[groupId] = set;
            }
            set.Add(behaviour);
        }

        private void RemoveBehaviourFromGroupIndex(ISyncBehaviour behaviour, int groupId)
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
                Behaviours = new Queue<ISyncBehaviour>(behavioursInGroup)
            };
            if (groupId == -1)
                DebugConsole.Log($"[OxySync] Global snapshot queued for player {playerId}: {behavioursInGroup.Count} behaviours");
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
                    // Only a snapshot that went out costs budget. A behaviour with no SyncVar
                    // (AnimSyncer on every duplicant and critter, WorkableSyncer, ...) used to
                    // burn one of the 16 slots per tick sending nothing, so filling a chunk a
                    // player just looked at took several times longer than it needed to.
                    if (!behaviour.IsDestroyed && SendBehaviourSnapshot(key.Player, key.Group, behaviour))
                        used++;
                }
                _snapshotBudgets[key.Player] = used;
                if (pending.Behaviours.Count == 0) _pendingSnapshots.Remove(key);
            }
        }

        /// <summary>True when a snapshot was actually sent; a behaviour with nothing to send costs no budget.</summary>
        private static bool SendBehaviourSnapshot(ulong playerId, int groupId, ISyncBehaviour behaviour)
        {
            int netId = behaviour.NetId;
            if (netId == 0) return false;
            int behaviourId = behaviour.BehaviourId;

            behaviour.RefreshSyncVars();
            int fieldCount = behaviour.SyncVarCount;
            if (fieldCount == 0) return false;

            var updates = new List<(int Hash, Variant Value)>();
            for (int i = 0; i < fieldCount; i++)
            {
                var field = behaviour.GetSyncVar(i);
                int fieldGroup = field.InterestGroup;
                if (fieldGroup == -1) fieldGroup = behaviour.InterestGroup;
                if (fieldGroup != groupId) continue;

                updates.Add((field.Hash, VariantHelper.ObjectToVariant(field.GetValue())));
            }

            if (updates.Count == 0) return false;

            ONI_Together.Networking.Transport.NetStats.RecordSyncFields(behaviour.UnderlyingType.Name, updates.Count, snapshot: true);
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
            return true;
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
        private void ResolveBehaviourId(ISyncBehaviour behaviour, int netId)
        {
            // A temporary collision at the old NetId must not permanently shift
            // the component's wire type when its identity is assigned/overridden.
            // Native ids are the type-name hash; a foreign behaviour keeps the id its mod gave it.
            if (behaviour is NativeSyncBehaviour)
            {
                var type = behaviour.UnderlyingType;
                behaviour.BehaviourId = (type.FullName ?? type.Name).GetHashCode();
            }
            int baseId = behaviour.BehaviourId;
            int id = baseId;

            while (_behaviourLookup.TryGetValue((netId, id), out var existing))
            {
                if (ReferenceEquals(existing, behaviour) || existing.IsDestroyed)
                {
                    _behaviourLookup.Remove((netId, id));
                    break;
                }

                id++;
            }

            if (id != baseId)
            {
                DebugConsole.LogWarning($"[OxySync] {behaviour.UnderlyingType.Name} on NetId {netId} collides with a live {_behaviourLookup[(netId, baseId)].UnderlyingType.Name}; filed under {id} instead of {baseId}. Packets addressed to the original id will not reach it.");
                behaviour.BehaviourId = id;
            }
        }

        private void RemoveLookupEntry((int, int) key, ISyncBehaviour behaviour)
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
                if (!Instance._nativeAdapters.TryGetValue(behaviour, out var adapter)) continue;
                if (!Instance._behaviours.Contains(adapter)) continue;

                if (behaviour.RegisteredNetId == newNetId &&
                    Instance._behaviourLookup.TryGetValue((newNetId, behaviour.BehaviourId), out var current) &&
                    ReferenceEquals(current, adapter))
                    continue;

                Instance.RemoveLookupEntry((behaviour.RegisteredNetId, behaviour.BehaviourId), adapter);
                Instance.ResolveBehaviourId(adapter, newNetId);
                behaviour.RegisteredNetId = newNetId;
                Instance._behaviourLookup[(newNetId, behaviour.BehaviourId)] = adapter;
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
            Instance._nativeAdapters.Clear();
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
        /// The behaviour a packet is addressed to, or null. Native and API (foreign) alike.
        ///
        /// The lookup is the fast path. When it misses - or holds a destroyed behaviour -
        /// the object behind the NetId is scanned for a native behaviour with the requested
        /// id, and that one is re-indexed so the next packet hits the fast path again; then
        /// the API bridge is asked. The old fallback took the first NetworkBehaviour on the
        /// object whatever its type; on a duplicant that is AnimSyncer, which has no sync
        /// vars, so position updates addressed to the position handler were dropped
        /// without a word. A miss is now logged once per address.
        /// </summary>
        public static ISyncBehaviour ResolveBehaviour(int netId, int behaviourId)
        {
            if (Instance != null &&
                Instance._behaviourLookup.TryGetValue((netId, behaviourId), out var found) &&
                found != null && !found.IsDestroyed)
                return found;

            if (!NetworkIdentityRegistry.TryGet(netId, out var identity, logFailure: false) || identity.IsNullOrDestroyed() || identity.gameObject.IsNullOrDestroyed())
            {
                // Once per address: what matters is which addresses are dead, not how
                // many packets hit them (282 267 lines in one host log).
                if (_fallbackWarned.Add((netId, behaviourId)))
                    DebugConsole.LogAggregated("OxySync.UnknownNetId", $"[OxySync] Packet for NetId {netId} behaviour {behaviourId} dropped: no such object here");
                return null;
            }

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
                // Not one of ours: a behaviour defined by a mod through the API.
                if (OxySync_API_Helper.TryGetForeignBehaviour(netId, behaviourId, out var foreign) && foreign != null)
                    return foreign;

                if (_fallbackWarned.Add((netId, behaviourId)))
                {
                    string present = string.Join(", ", candidates.Where(c => !c.IsNullOrDestroyed()).Select(c => $"{c.GetType().Name}={c.BehaviourId}"));
                    DebugConsole.LogWarning($"[OxySync] No behaviour {behaviourId} on NetId {netId} ({identity.gameObject.name}); present: [{present}]. Packet dropped.");
                }
                return null;
            }

            if (Instance == null)
                return new NativeSyncBehaviour(match);

            var adapter = Instance.AdapterFor(match);
            Instance.RemoveLookupEntry((match.RegisteredNetId, match.BehaviourId), adapter);
            match.RegisteredNetId = netId;
            Instance._behaviourLookup[(netId, behaviourId)] = adapter;
            if (!Instance._behaviours.Contains(adapter))
                Instance._behaviours.Add(adapter);
            if (_fallbackWarned.Add((netId, behaviourId)))
                DebugConsole.Log($"[OxySync] Re-indexed {match.GetType().Name} on NetId {netId} ({identity.gameObject.name}); it was not in the lookup under its id.");

            return adapter;
        }
    }
}
