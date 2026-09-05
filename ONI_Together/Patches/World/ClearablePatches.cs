using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
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

		[HarmonyPatch(typeof(Clearable), "OnAbsorb")]
		public static class ClearableOnAbsorbPatch
		{
			public static void Prefix()
			{
				SuppressItemPackets++;
			}

			public static void Finalizer()
			{
				SuppressItemPackets--;
			}
		}

		[HarmonyPatch(typeof(Clearable), nameof(Clearable.MarkForClear))]
		public static class ClearableMarkForClearPatch
		{
			public static void Postfix(Clearable __instance, bool restoringFromSave, bool allowWhenStored)
			{
				using var _ = Profiler.Scope();

				if (restoringFromSave) return;
				if (SuppressItemPackets > 0) return;
				if (ClearableActionPacket.ProcessingIncoming) return;
				if (ClearPacket.ProcessingIncoming) return;
				if (!MultiplayerSession.InActiveSession) return;
				if (__instance == null || __instance.gameObject == null) return;

				Send(__instance, true);
			}
		}

		[HarmonyPatch(typeof(Clearable), nameof(Clearable.CancelClearing))]
		public static class ClearableCancelClearingPatch
		{
			public static void Postfix(Clearable __instance)
			{
				using var _ = Profiler.Scope();

				if (SuppressItemPackets > 0) return;
				if (ClearableActionPacket.ProcessingIncoming) return;
				if (ClearPacket.ProcessingIncoming) return;
				if (!MultiplayerSession.InActiveSession) return;
				if (__instance == null || __instance.gameObject == null) return;

				Send(__instance, false);
			}
		}

		private static void Send(Clearable clearable, bool marked)
		{
			var identity = clearable.gameObject.GetNetIdentity();
			int netId = identity != null ? identity.NetId : 0;
			int cell = Grid.PosToCell(clearable.gameObject);

			var packet = new ClearableActionPacket
			{
				NetId = netId,
				Cell = cell,
				IsMarked = marked
			};

			if (MultiplayerSession.IsHost)
				PacketSender.SendToAllClients(packet);
			else
				PacketSender.SendToHost(packet);
		}
	}
}
