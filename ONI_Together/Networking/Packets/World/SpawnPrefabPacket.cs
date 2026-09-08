using System;
using System.IO;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.OxySync.Components;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Synchronization;
using ONI_Together.Scripts.Creatures;
using ONI_Together.Scripts.Duplicants;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Packets.World;

public class SpawnPrefabPacket : IPacket
{
    public static bool ProcessingIncoming;

    public int NetId;
    public int Hash;
    public string PrefabName = string.Empty;
    public Vector3 Position;
    public bool IsActive = true;

    public bool HasElementData = false;
    public float Mass;
    public float Temperature;
    public byte DiseaseIndex;
    public int DiseaseCount;

    /// <summary>
    /// Required by the receiver, and missing.
    ///
    /// PacketRegistry.Create builds every incoming packet with
    /// Activator.CreateInstance and then deserialises into it, so a type whose only
    /// constructors take arguments cannot be received at all - each one throws
    /// "Default constructor not found" before Deserialize is reached.
    ///
    /// SpawnUtils.KNetInstantiate sends this packet on both of its overloads, so the
    /// host announces prefab and element spawns that no client can apply.
    /// </summary>
    public SpawnPrefabPacket() { }

    public SpawnPrefabPacket(int netId, int hash, Vector3 position, string prefabName = null)
    {
        NetId = netId;
        Hash = hash;
        PrefabName = prefabName ?? string.Empty;
        Position = position;
        HasElementData = false;
    }
    
    public SpawnPrefabPacket(int netId, int hash, Vector3 position, float mass, float temperature, byte diseaseIndex, int diseaseCount, string prefabName = null)
    {
        NetId = netId;
        Hash = hash;
        PrefabName = prefabName ?? string.Empty;
        Position = position;
        HasElementData = true;
        Mass = mass;
        Temperature = temperature;
        DiseaseIndex = diseaseIndex;
        DiseaseCount = diseaseCount;
    }
    
    public void Serialize(BinaryWriter writer)
    {
        using var _ = Profiler.Scope();

        writer.Write(NetId);
        writer.Write(Hash);
        writer.Write(PrefabName ?? string.Empty);
        writer.Write(Position);
        writer.Write(IsActive);
        writer.Write(HasElementData);
        // Always written: a prefab item (a harvested crop, a seed, meat) has a mass of
        // its own too. Announced without it, a client instantiated the prefab at its
        // default weight - 1 kg for a 12 kg lettuce harvest - and counted the wrong
        // calories and resource totals for the rest of the session.
        writer.Write(Mass);
        writer.Write(Temperature);
        writer.Write(DiseaseIndex);
        writer.Write(DiseaseCount);
    }

    public void Deserialize(BinaryReader reader)
    {
        using var _ = Profiler.Scope();

        NetId = reader.ReadInt32();
        Hash = reader.ReadInt32();
        PrefabName = reader.ReadString();
        Position = reader.ReadVector3();
        IsActive = reader.ReadBoolean();
        HasElementData = reader.ReadBoolean();
        Mass = reader.ReadSingle();
        Temperature = reader.ReadSingle();
        DiseaseIndex = reader.ReadByte();
        DiseaseCount = reader.ReadInt32();
    }

    /// <summary>The host's mass and temperature for a prefab item, applied after activation the way a harvest sets them.</summary>
    private void ApplyPrimaryData(GameObject go)
    {
        if (HasElementData || Mass <= 0f || go == null) return;
        var pe = go.GetComponent<PrimaryElement>();
        if (pe == null) return;
        pe.Mass = Mass;
        if (Temperature > 0f) pe.Temperature = Temperature;
        if (DiseaseIndex != byte.MaxValue && DiseaseCount > 0) pe.AddDisease(DiseaseIndex, DiseaseCount, "Multiplayer Sync");
    }

    public void OnDispatched()
    {
        using var _ = Profiler.Scope();

        if (MultiplayerSession.IsHost) return;

        ProcessingIncoming = true;
        try
        {
            // If already registered with this NetId on client, do not spawn a duplicate
                        if (NetId != 0 && NetworkIdentityRegistry.TryGet(NetId, out var existing) && existing != null)
                        {
                            return;
                        }

                        // The host announced this NetId as a WorldGenSpawner object: our own
                        // WorldGenSpawner makes the copy, and WorldGenSpawnMap pairs the ids.
                        if (WorldGenSpawnMap.IsPending(NetId))
                        {
                            DebugConsole.Log($"[SpawnPrefabPacket] '{PrefabName}' (NetId: {NetId}) is a WorldGenSpawner object; waiting for the local spawn");
                            return;
                        }

            GameObject go = null;
            if (HasElementData)
            {
                Element element = null;
                if (!string.IsNullOrEmpty(PrefabName))
                    element = ElementLoader.FindElementByName(PrefabName);
                if (element == null)
                    element = ElementLoader.GetElement(new Tag(Hash));

                if (element != null && element.substance != null)
                {
                    go = element.substance.SpawnResource(Position, Mass, Temperature, DiseaseIndex, DiseaseCount);
                }
            }
            
            if (go == null)
            {
                GameObject prefab = null;
                string lookupName = PrefabName;
                if (!string.IsNullOrEmpty(lookupName) && lookupName.Contains("|"))
                    lookupName = lookupName.Split('|')[0];

                if (!string.IsNullOrEmpty(lookupName))
                    prefab = Assets.GetPrefab(new Tag(lookupName)) ?? Assets.GetPrefab(lookupName);
                if (prefab == null)
                    prefab = Assets.GetPrefab(new Tag(Hash));

                if (prefab == null)
                {
                    DebugConsole.LogWarning($"[SpawnPrefabPacket] Prefab not found (Name: '{PrefabName}', Hash: {Hash})");
                    return;
                }

                // If this is a building prefab, it must be constructed via BuildingDef.Build to properly initialize PrimaryElement
                if (prefab.TryGetComponent<Building>(out var buildingComp) || prefab.GetComponent<BuildingComplete>() != null)
                {
                    BuildingDef def = buildingComp != null ? buildingComp.Def : Assets.GetBuildingDef(prefab.name.Replace("Complete", ""));
                    if (def != null)
                    {
                        int cell = Grid.PosToCell(Position);
                        if (Grid.IsValidCell(cell))
                        {
                            go = def.Build(cell, Orientation.Neutral, null, def.DefaultElements(), Temperature > 0 ? Temperature : 293.15f, playsound: false, GameClock.Instance.GetTime());
                        }
                    }
                }

                if (go == null)
                {
                    // If this is a duplicant prefab, instantiate using MinionStartingStats.Deliver
                    if (prefab.GetComponent<MinionIdentity>() != null || prefab.HasTag(GameTags.BaseMinion) || (PrefabName != null && PrefabName.StartsWith("Minion", StringComparison.OrdinalIgnoreCase)))
                    {
                        string personalityId = null;
                        string dupeName = null;
                        int voiceIdx = -1;

                        if (!string.IsNullOrEmpty(PrefabName) && PrefabName.Contains("|"))
                        {
                            var parts = PrefabName.Split('|');
                            if (parts.Length > 1) personalityId = parts[1];
                            if (parts.Length > 2) dupeName = parts[2];
                            if (parts.Length > 3 && int.TryParse(parts[3], out int v)) voiceIdx = v;
                        }
                        else if (!string.IsNullOrEmpty(PrefabName) && !string.Equals(PrefabName, "Minion", StringComparison.OrdinalIgnoreCase))
                        {
                            personalityId = PrefabName;
                        }

                        Personality personality = null;
                        if (!string.IsNullOrEmpty(personalityId))
                        {
                            personality = Db.Get()?.Personalities?.TryGet(personalityId);
                            if (personality == null)
                                personality = Db.Get()?.Personalities?.TryGet(new HashedString(personalityId));
                        }
                        if (personality == null && Db.Get()?.Personalities?.resources != null && Db.Get().Personalities.resources.Count > 0)
                        {
                            personality = Db.Get().Personalities.resources[0];
                        }

                        if (personality != null)
                        {
                            var stats = new MinionStartingStats(personality);
                            if (!string.IsNullOrEmpty(dupeName))
                                stats.Name = dupeName;
                            if (voiceIdx >= 0)
                                stats.voiceIdx = voiceIdx;

                            go = stats.Deliver(Position);
                        }
                        if (go == null)
                        {
                                                        go = Util.KInstantiate(prefab, Position);
                                                        ActivateReplica(go);
                                                    }

                        var netIdComp = go.AddOrGet<NetworkIdentity>();
                        netIdComp.OverrideNetId(NetId);
                    }
                    else
                    {
                        go = Util.KInstantiate(prefab, Position);
                        var netIdComp = go.AddOrGet<NetworkIdentity>();
                        netIdComp.OverrideNetId(NetId);
                        ActivateReplica(go);
                        ApplyPrimaryData(go);
                    }
                }
            }
            
            if (go != null)
            {
                // Unconditional: a substance chunk's locally computed id can EQUAL the host's
                // (same prefab, cell, mass and temperature hash the same on both sides), and
                // skipping the override then left the identity marked as this side's own -
                // the local-item guard removed the host's dug ore and dropped seeds 5 s later.
                var identity = go.AddOrGet<NetworkIdentity>();
                identity.OverrideNetId(NetId);

                if (go.GetComponent<MinionIdentity>() != null || go.HasTag(GameTags.BaseMinion))
                {
                    go.AddOrGet<OxySyncEntityPositionHandler>();
                    go.AddOrGet<AnimSyncer>();
                    go.AddOrGet<MinionMultiplayerInitializer>();
                }
                else if (go.GetComponent<CreatureBrain>() != null || go.HasTag(GameTags.Creature))
                {
                    go.AddOrGet<OxySyncEntityPositionHandler>();
                    go.AddOrGet<AnimSyncer>();
                    go.AddOrGet<CreatureMultiplayerInitializer>();
                }

                if (DebugConsole.IsVerbose) DebugConsole.LogVerbose($"[SpawnPrefabPacket] Spawned entity '{go.name}' (NetId: {NetId}) at {Position}");
                
                // Race condition guard: ONLY for loose substance/ore ground resources, NEVER destroy living creatures / plants / minions / buildings!
                if (HasElementData || go.GetComponent<SubstanceChunk>() != null)
                {
                    if (GroundItemPickedUpPacket.TryConsumePending(NetId) || StorageItemPacket.TryConsumePending(NetId))
                    {
                        DebugConsole.Log($"[SpawnPrefabPacket] Consumed pending pickup for resource NetId {NetId}");
                        Util.KDestroyGameObject(go);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DebugConsole.LogError($"[SpawnPrefabPacket] Exception spawning prefab (Name='{PrefabName}', Hash={Hash}, NetId={NetId}): {ex}");
        }
                finally
                {
                    ProcessingIncoming = false;
                }
            }

            /// <summary>
            /// A replica is always activated. Nothing ever sends "activate it later", so an
            /// inactive copy is a copy whose components never run Awake: registered under the
            /// host's NetId, invisible, and a NullReferenceException for the first order that
            /// reaches it (StandardWorker.StartWork -> KMonoBehaviour.Subscribe, see
            /// WorldGenSpawnMap). IsActive = false only ever came from Scenario.SpawnPrefab,
            /// which no longer sends this packet; log if it shows up again.
            /// </summary>
            private void ActivateReplica(GameObject go)
            {
                if (!IsActive)
                    DebugConsole.LogWarning($"[SpawnPrefabPacket] '{PrefabName}' (NetId: {NetId}) arrived with IsActive = false; activating anyway");
                go.SetActive(true);
            }
        }