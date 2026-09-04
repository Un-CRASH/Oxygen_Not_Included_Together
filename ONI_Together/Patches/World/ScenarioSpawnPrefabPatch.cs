using HarmonyLib;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Networking.Synchronization;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Patches.World
{
	/// <summary>
	/// Scenario.SpawnPrefab is the instantiation step of WorldGenSpawner (through
	/// TemplateLoader) and of a few loot spawners (Rottable, Butcherable, DeathLoot,
	/// CargoBay, SetLocker, GravitasLocker). It returns the object before anyone
	/// activates it.
	///
	/// This used to send a SpawnPrefabPacket from here, with IsActive = false, and the
	/// client kept an inactive copy forever - see WorldGenSpawnMap. Now:
	/// - a WorldGenSpawner spawn only announces its NetId, because the client's own
	///   WorldGenSpawner creates the object itself;
	/// - everything else is left alone: those are pickupables, and Pickupable.OnSpawn
	///   announces them once they are active and carry their real mass.
	/// On the client the same postfix is where its own WorldGenSpawner copy is paired
	/// with the host's id.
	/// </summary>
	[HarmonyPatch(typeof(Scenario), nameof(Scenario.SpawnPrefab), [typeof(int), typeof(int), typeof(int), typeof(string), typeof(Grid.SceneLayer)])]
	public static class ScenarioSpawnPrefabPatch
	{
		public static void Postfix(GameObject __result)
		{
			using var _ = Profiler.Scope();

			if (__result == null || !WorldGenSpawnMap.InWorldGenSpawn)
				return;
			if (!MultiplayerSession.InActiveSession)
				return;

			int cell = Grid.PosToCell(__result);
			if (!Grid.IsValidCell(cell))
				return;
			string prefab = __result.PrefabID().Name;

			if (MultiplayerSession.IsClient)
			{
				WorldGenSpawnMap.OnLocalSpawn(__result, prefab, cell);
				return;
			}

			if (!MultiplayerSession.IsHostInSession)
				return;

			var identity = __result.AddOrGet<NetworkIdentity>();
			if (identity.NetId == 0)
				identity.RegisterIdentity();
			if (identity.NetId == 0)
				return;

			PacketSender.SendToAllClients(new WorldGenSpawnPacket
			{
				NetId = identity.NetId,
				PrefabTag = prefab,
				Cell = cell,
			});
		}
	}

	/// <summary>Marks the window in which Scenario.SpawnPrefab is WorldGenSpawner's doing.</summary>
	[HarmonyPatch(typeof(WorldGenSpawner.Spawnable), "Spawn")]
	public static class WorldGenSpawnableSpawnPatch
	{
		public static void Prefix()
		{
			WorldGenSpawnMap.InWorldGenSpawn = true;
		}

		public static void Finalizer()
		{
			WorldGenSpawnMap.InWorldGenSpawn = false;
		}
	}
}
