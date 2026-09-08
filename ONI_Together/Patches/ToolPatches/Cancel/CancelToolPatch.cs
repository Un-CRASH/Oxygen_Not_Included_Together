using HarmonyLib;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Tools;
using ONI_Together.Networking.Packets.Tools.Cancel;
using Shared.Profiling;

namespace ONI_Together.Patches.ToolPatches.Cancel
{
	[HarmonyPatch(typeof(CancelTool), nameof(CancelTool.OnDragTool))]
	public static class CancelToolPatch
	{
		private static OrderApplyScope.Token _drag;

		// The cell is synced by CancelPacket; the per-building packets the game's
		// OnCancel events would add on top of it are duplicates (see OrderApplyScope).
		public static void Prefix()
		{
			_drag = OrderApplyScope.EnterLocalDrag();
		}

		public static void Finalizer()
		{
			_drag.Dispose();
		}

		public static void Postfix(int cell, int distFromOrigin)
		{
			using var _ = Profiler.Scope();

			if (!MultiplayerSession.InActiveSession)
				return;

			//prevent recursion
			if (CancelPacket.ProcessingIncoming || OrderApplyScope.IsApplying)
				return;
			PacketSender.SendToAllOtherPeers(new CancelPacket() { cell = cell, distFromOrigin = distFromOrigin });
		}
	}
}
