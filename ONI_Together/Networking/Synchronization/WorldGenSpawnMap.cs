using System.Collections.Generic;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using UnityEngine;

namespace ONI_Together.Networking.Synchronization
{
	/// <summary>
	/// Pairs the host's NetId with the client's own copy of a WorldGenSpawner object.
	///
	/// WorldGenSpawner (the thing that materialises plants, POI props, sculpture rocks
	/// and loose seeds when fog of war lifts) runs on every peer, so the client creates
	/// its own copy of each such object a second or so after the host does - under a
	/// local id, because the "deterministic" workable id mixes in mass, temperature and
	/// registration order. Until now the host also sent a SpawnPrefabPacket from the
	/// Scenario.SpawnPrefab postfix, before the object was activated, so the client
	/// ended up with a second, never-activated copy registered under the host's id.
	/// Any order addressed to that id (uproot, harvest, demolish, PlantSyncer state)
	/// landed on a component that never ran Awake; StandardWorker.StartWork then
	/// threw from KMonoBehaviour.Subscribe and the game showed its crash screen.
	///
	/// The host now sends only the id; the client attaches it to the copy it makes
	/// itself, whichever of the two arrives first.
	/// </summary>
	public static class WorldGenSpawnMap
	{
		/// <summary>True while WorldGenSpawner.Spawnable.Spawn is on the stack.</summary>
		public static bool InWorldGenSpawn;

		private static readonly Dictionary<(string, int), int> _hostIdByKey = new();
		private static readonly Dictionary<int, (string, int)> _keyByHostId = new();
		private static readonly Dictionary<(string, int), GameObject> _localByKey = new();

		public static bool IsPending(int netId) => netId != 0 && _keyByHostId.ContainsKey(netId);

		/// <summary>Client: the host told us which id its copy of (prefab, cell) has.</summary>
		public static void OnHostMapping(int netId, string prefab, int cell)
		{
			if (netId == 0 || string.IsNullOrEmpty(prefab))
				return;

			var key = (prefab, cell);
			if (_localByKey.TryGetValue(key, out var go))
			{
				_localByKey.Remove(key);
				if (!go.IsNullOrDestroyed())
				{
					Adopt(go, netId, prefab, cell);
					return;
				}
			}

			if (_hostIdByKey.TryGetValue(key, out var previous) && previous != netId)
				_keyByHostId.Remove(previous);
			_hostIdByKey[key] = netId;
			_keyByHostId[netId] = key;
		}

		/// <summary>Client: our own WorldGenSpawner just created (prefab, cell).</summary>
		public static void OnLocalSpawn(GameObject go, string prefab, int cell)
		{
			if (go == null || string.IsNullOrEmpty(prefab))
				return;

			var key = (prefab, cell);
			if (_hostIdByKey.TryGetValue(key, out var netId))
			{
				_hostIdByKey.Remove(key);
				_keyByHostId.Remove(netId);
				Adopt(go, netId, prefab, cell);
				return;
			}

			_localByKey[key] = go;
		}

		private static void Adopt(GameObject go, int netId, string prefab, int cell)
		{
			var identity = go.AddOrGet<NetworkIdentity>();
			if (identity.NetId != netId)
				identity.OverrideNetId(netId);
			DebugConsole.Log($"[WorldGenSpawn] {prefab} at cell {cell} adopted host NetId {netId}");
		}

		public static void Clear()
		{
			InWorldGenSpawn = false;
			_hostIdByKey.Clear();
			_keyByHostId.Clear();
			_localByKey.Clear();
		}
	}
}
