using ONI_Together.DebugTools;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking
{
	public static class NetIdHelper
	{
		/// <summary>
		/// Generates a deterministic NetID for a building based on its location and object layer.
		/// Uses stable prefab/layer/cell data, independent of local spawn order.
		/// </summary>
		public static int GetDeterministicBuildingId(GameObject go)
		{
			using var _ = Profiler.Scope();

			if (go == null) return 0;

			int cell = Grid.PosToCell(go);
			if (!Grid.IsValidCell(cell)) return 0;

			if (!go.TryGetComponent<Building>(out var building) || building.Def == null)
				return 0;

			int hash = StableHash("Building|" + go.PrefabID().Name + "|" + building.GetType().FullName);
			hash = unchecked((hash * 16777619) ^ (int)building.Def.ObjectLayer);
			hash = unchecked((hash * 16777619) ^ cell);
			return hash == 0 ? int.MinValue : hash;
		}
		public static int GetDeterministicWorkableId(GameObject go)
		{
			using var _ = Profiler.Scope();

			if (go == null) return 0;

			int cell = Grid.PosToCell(go);
			if (!Grid.IsValidCell(cell)) return 0;

			if (!go.TryGetComponent<Workable>(out var workable))
				return 0;

			// Pickupables move and are paired with the host's saved/announced identity.
			// Stationary workables (including Diggable, which has no PrimaryElement)
			// must not depend on registration order or mutable work/element state.
			if (!go.TryGetComponent<Pickupable>(out var pickupable))
			{
				int stationaryId = StableHash("Workable|" + go.PrefabID().Name + "|" + workable.GetType().FullName);
				stationaryId = unchecked((stationaryId * 16777619) ^ cell);
				return stationaryId == 0 ? int.MinValue : stationaryId;
			}

			int hash = GetDeterministicEntityId(go,false,false) ^ workable.GetType().Name.GetHashCode() ^ ((int)workable.workTime).GetHashCode()
				^ workable.multitoolHitEffectTag.GetHashCode() ^ workable.multitoolContext.GetHashCode();
			int breakoff = 0;
			while (NetworkIdentityRegistry.Exists(hash + breakoff))
			{
				breakoff++;
			}
			hash += breakoff;
			if (DebugConsole.IsVerbose) DebugConsole.LogVerbose($"[NetIdHelper] Registered workable {go.PrefabID()} with id {hash} ({workable.GetType().Name}) at cell {cell}");
			return hash;
		}


		// Explicit string hashing: network identities must agree across processes.
		private static int StableHash(string value)
		{
			unchecked
			{
				int hash = (int)2166136261;
				foreach (char c in value)
					hash = (hash ^ c) * 16777619;
				return hash;
			}
		}

		public static int GetDeterministicEntityId(GameObject go, bool useBreakOff = true, bool useCell = true)
		{
			using var _ = Profiler.Scope();

			if (go == null) return 0;

			int cell = Grid.PosToCell(go);
			if (!Grid.IsValidCell(cell))
				return 0;

			// Keep the pickupable calculation below: moving stacks are paired by the
			// save/spawn packets. Static entities must not hash translated names,
			// temperature or mass, nor borrow an ID from local registration order.
			if (!go.TryGetComponent<Pickupable>(out var pickupable))
			{
				int staticId = StableHash("Entity|" + go.PrefabID().Name);
				staticId = unchecked((staticId * 16777619) ^ cell);
				return staticId == 0 ? int.MinValue : staticId;
			}
			if (!go.TryGetComponent<PrimaryElement>(out var primaryElement)) return 0;

			int hash = go.PrefabID().GetHashCode();
			if(useCell)
				hash = hash ^ cell.GetHashCode();
			hash = hash ^ go.GetProperName().GetHashCode() ^ primaryElement.ElementID.GetHashCode() ^ primaryElement.Mass.GetHashCode() ^ primaryElement.Temperature.GetHashCode();

			int breakoff = 0;
			if (useBreakOff)
			{
				while (NetworkIdentityRegistry.Exists(hash + breakoff))
				{
					breakoff++;
				}
			}
			hash += breakoff;
			if (useBreakOff && DebugConsole.IsVerbose)
				DebugConsole.LogVerbose($"[NetIdHelper] Registered entity {go.PrefabID()} with id {hash}");
			return hash;
		}
	}
}
