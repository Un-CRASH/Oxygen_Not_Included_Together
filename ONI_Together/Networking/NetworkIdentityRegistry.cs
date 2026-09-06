using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using System;
using System.Collections.Generic;
using Shared.Profiling;
using UnityEngine;
using ONI_Together.Networking.Packets.World;

namespace ONI_Together.Networking
{
	public static class NetworkIdentityRegistry
	{
		private static readonly Dictionary<int, NetworkIdentity> identities = new Dictionary<int, NetworkIdentity>();

		private static int _lookupFailCount = 0;

		public static int Count => identities?.Count ?? 0;

		public static int Register(NetworkIdentity entity)
		{
			using var _ = Profiler.Scope();

			int id, attempt = 0;
			do
			{
				id = Guid.NewGuid().GetHashCode() + attempt++;
			} while (id == 0 || Exists(id));

			identities[id] = entity;
			return id;
		}

		public static bool Unregister(int netId, NetworkIdentity owner)
		{
			using var _ = Profiler.Scope();

			// A stale/overridden identity must never remove the new owner of its ID.
			return Owns(netId, owner) && identities.Remove(netId);
		}

		public static bool Owns(int netId, NetworkIdentity owner) =>
			identities.TryGetValue(netId, out var existing) && ReferenceEquals(existing, owner);


		public static int RegisterExisting(NetworkIdentity entity, int netId)
		{
			using var _ = Profiler.Scope();

			if (netId == 0) return Register(entity);
			if (TryGet(netId, out var existing, logFailure: false) && !ReferenceEquals(existing, entity))
			{
				// Duplicate saved/cloned IDs cannot address two objects. The host saves
				// and announces this new ID; a client later adopts the host's mapping.
				int replacement = Register(entity);
				DebugConsole.LogAggregated("Registry.LiveCollision", $"[Registry] NetId {netId} belongs to {existing.name}; assigned {replacement} to {entity.name} at cell {Grid.PosToCell(entity.gameObject)}");
				return replacement;
			}
			identities[netId] = entity;
			return netId;
		}

		public static void RegisterOverride(NetworkIdentity entity, int netId)
		{
			using var _ = Profiler.Scope();

			if (netId == 0) return;
			if (TryGet(netId, out var existing, logFailure: false) && !ReferenceEquals(existing, entity))
			{
				// A host mapping wins over a locally generated ID, but the displaced
				// object stays alive and indexed until its own mapping arrives.
				int replacement = Register(existing);
				existing.OverrideNetId(replacement);
				DebugConsole.LogAggregated("Registry.OverrideCollision", $"[Registry] Host NetId {netId} adopted by {entity.name}; moved {existing.name} to {replacement}");
			}
			identities[netId] = entity;
		}
		public static bool Exists(int netId) => TryGet(netId, out var entity, logFailure: false);

		/// <summary>
		/// Re-index every live NetworkIdentity in the scene under the NetId it carries.
		/// NetworkConfig.Stop clears this registry, but the objects of a loaded world stay,
		/// and RegisterIdentity is guarded by IsRegistered, so after a server restart in the
		/// same world the host resolved nothing: Count went from 5318 to 0 in the log and
		/// crept back only with newly spawned objects until the save was reloaded.
		/// Inactive objects are included: items inside storages are inactive and are
		/// addressed by NetId too. Returns the number of entries.
		/// </summary>
		public static int RebuildFromScene()
		{
			using var _ = Profiler.Scope();

			identities.Clear();
			_lookupFailCount = 0;

			int collisions = 0;
			// Sorted by instance id, so two live objects with one id resolve the same way
			// on every rebuild (the older object, which registered first, wins).
			foreach (var identity in UnityEngine.Object.FindObjectsByType<NetworkIdentity>(FindObjectsInactive.Include, FindObjectsSortMode.InstanceID))
			{
				if (identity == null || identity.NetId == 0) continue;
				if (Exists(identity.NetId)) collisions++;
				identity.RegisterIdentity();
			}

			if (collisions > 0)
				DebugConsole.LogWarning($"[NetEntityRegistry] {collisions} objects share a NetId with another live object; each object now has its own id");

			return identities.Count;
		}



		public static bool TryGet(int netId, out NetworkIdentity entity, bool logFailure = true)
		{
			using var _ = Profiler.Scope();

			bool found = identities.TryGetValue(netId, out entity);
			if (!found && logFailure)
			{
				_lookupFailCount++;
				DebugConsole.LogAggregated("Registry.LookupFailed", $"[Registry] Lookup failed (#{_lookupFailCount}): NetId {netId} not found. Count: {identities.Count}");
			}
			
			if (entity.IsNullOrDestroyed() || entity.gameObject.IsNullOrDestroyed())
			{
				identities.Remove(netId);
				entity = null;
				return false;
			}
			
			return found;
		}

		public static bool TryGetComponent<T>(int netId, out T component)
		{
			using var _ = Profiler.Scope();

			component = default(T);
			if (!TryGet(netId, out var ni))
				return false;
			if(ni.gameObject.IsNullOrDestroyed())
				return false;
			return ni.gameObject.TryGetComponent<T>(out component);
		}
		public static bool TryGetComponent<T>(NetworkIdentity ni, out T component)
		{
			using var _ = Profiler.Scope();

			component = default(T);
			if (ni.IsNullOrDestroyed() || ni.gameObject.IsNullOrDestroyed())
				return false;
			return ni.gameObject.TryGetComponent<T>(out component);
		}

		public static void Clear()
		{
			using var _ = Profiler.Scope();

			identities.Clear();
			_lookupFailCount = 0;
			// TODO Rope into 1
			GroundItemPickedUpPacket.ClearPending();
			StorageItemPacket.ClearPending();
		}

		public static IEnumerable<NetworkIdentity> AllIdentities => identities.Values;
	}
}
