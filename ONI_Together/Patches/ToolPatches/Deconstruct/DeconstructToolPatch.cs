using HarmonyLib;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Tools;
using ONI_Together.Networking.Packets.Tools.Cancel;
using ONI_Together.Networking.Packets.Tools.Deconstruct;
using Shared.Profiling;

namespace ONI_Together.Patches.ToolPatches.Deconstruct
{
	[HarmonyPatch(typeof(DeconstructTool), nameof(DeconstructTool.OnDragTool))]
	public static class DeconstructToolPatch
	{
		private static OrderApplyScope.Token _drag;

		// The cell is synced by DeconstructPacket; the per-building packets the game's
		// QueueDeconstruction calls would add on top of it are duplicates.
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
			if (DeconstructPacket.ProcessingIncoming || OrderApplyScope.IsApplying)
				return;
			PacketSender.SendToAllOtherPeers(new DeconstructPacket() { cell = cell, distFromOrigin = distFromOrigin });
		}
	}
}
