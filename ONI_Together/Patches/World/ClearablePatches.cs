using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Tools;
using ONI_Together.Networking.Packets.Tools.Clear;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Patches.World
{
	public static class ClearablePatches
	{
		/// <summary>
		/// Greater than zero while a code path marks items but must not send a
		/// ClearableActionPacket per item:
		///  - Clearable.OnAbsorb re-marks the surviving pile whenever a marked pile merges
		///    into another one. Piles merge all the time, a merged pile never carries the
		///    id the other side knows, and every one of those packets ended in
		///    "Target not found": 29 500 on the host and 15 700 on one client in a session.
		///  - ClearTool.OnDragTool is synced by cell through ClearPacket already; the
		///    per-item packets it produced on top were duplicates.
		/// </summary>
		internal static int SuppressItemPackets;

		internal static void ResetState() => SuppressItemPackets = 0;

		[HarmonyPatch(typeof(Clearable), "OnAbsorb")]
		public static class ClearableOnAbsorbPatch
		{
			public static void Prefix()
			{
				SuppressItemPackets++;
			}

			public static void Finalizer()
			{
				if (SuppressItemPackets > 0) SuppressItemPackets--;
			}
		}

		/// <summary>
		/// A Harmony postfix runs whether or not the game method did anything. Both
		/// Clearable methods return at once when there is nothing to change, and the game
		/// calls them constantly for no-ops: Pickupable.HandleSolidCell cancels the sweep
		/// of every item sitting in a solid cell on every cell or solid change around
		/// it, so a pile buried under a floor produced a packet per change, for hours
		/// (100-200 "Target not found" per minute on the host). Only a mark that actually
		/// changed is an order worth sending.
		/// </summary>
		[HarmonyPatch(typeof(Clearable), nameof(Clearable.MarkForClear))]
		public static class ClearableMarkForClearPatch
		{
			public static void Prefix(Clearable __instance, out bool __state)
			{
				__state = __instance != null && __instance.isMarkedForClear;
			}

			public static void Postfix(Clearable __instance, bool restoringFromSave, bool allowWhenStored, bool __state)
			{
				using var _ = Profiler.Scope();

				if (restoringFromSave) return;
				if (__instance == null || __instance.gameObject == null) return;
				if (__instance.isMarkedForClear == __state) return;
				if (!ShouldSend()) return;
				if (IsStored(__instance)) return;

				Send(__instance, true);
			}
		}

		[HarmonyPatch(typeof(Clearable), nameof(Clearable.CancelClearing))]
		public static class ClearableCancelClearingPatch
		{
			public static void Prefix(Clearable __instance, out bool __state)
			{
				__state = __instance != null && __instance.isMarkedForClear;
			}

			public static void Postfix(Clearable __instance, bool __state)
			{
				using var _ = Profiler.Scope();

				if (__instance == null || __instance.gameObject == null) return;
				if (__instance.isMarkedForClear == __state) return;
				if (!ShouldSend()) return;

				Send(__instance, false);
			}
		}

		private static bool ShouldSend()
		{
			if (SuppressItemPackets > 0) return false;
			if (ClearableActionPacket.ProcessingIncoming) return false;
			if (ClearPacket.ProcessingIncoming) return false;
			if (OrderApplyScope.SuppressEchoes) return false;
			return MultiplayerSession.InActiveSession;
		}

		/// <summary>
		/// An item inside a storage has no counterpart on clients (StorageItemPacket destroys
		/// it there), so a mark on it can resolve nowhere. SweepBotStation.OnStorageChanged
		/// re-marks everything in its bin on every change: 41 499 such packets reached the
		/// host in one session, none resolved, and each was relayed to every client.
		/// Only marks are skipped: a cancel for an item some client still holds must arrive.
		/// </summary>
		private static bool IsStored(Clearable clearable)
		{
			return clearable.TryGetComponent<Pickupable>(out var pickupable) && pickupable.storage != null;
		}

		private static void Send(Clearable clearable, bool marked)
		{
			// The id the item already has, never one minted on the way past: GetNetIdentity
			// attaches a NetworkIdentity with a locally computed id, which the other side
			// has never heard of (see PrioritizablePatch).
			var go = clearable.gameObject;
			var identity = go.GetExistingNetIdentity();
			int netId = identity != null ? identity.NetId : 0;
			int cell = Grid.PosToCell(go);
			if (!Grid.IsValidCell(cell))
				return;

			var packet = new ClearableActionPacket
			{
				NetId = netId,
				Cell = cell,
				IsMarked = marked,
				PrefabID = go.TryGetComponent<KPrefabID>(out var prefab) ? prefab.PrefabTag.ToString() : string.Empty,
			};

			if (MultiplayerSession.IsHost)
				PacketSender.SendToAllClients(packet);
			else
				PacketSender.SendToHost(packet);
		}
	}
}
