using HarmonyLib;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Tools.Clear;
using ONI_Together.Patches.World;
using Shared.Profiling;

namespace ONI_Together.Patches.ToolPatches.Clear
{
	[HarmonyPatch(typeof(ClearTool), "OnDragTool")]
	public static class ClearTool_OnDragTool_Patch
	{
		public static void Prefix(int cell, int distFromOrigin)
		{
			using var _ = Profiler.Scope();

			// The cell is synced below; the per-item packets Clearable.MarkForClear would
			// add on top of it are duplicates (see ClearablePatches.SuppressItemPackets).
			ClearablePatches.SuppressItemPackets++;

			if (!MultiplayerSession.InActiveSession)
				return;

			if (!Grid.IsValidCell(cell))
				return;

			if (ClearPacket.ProcessingIncoming)
				return;

			PacketSender.SendToAllOtherPeers(new ClearPacket { cell = cell, distFromOrigin = distFromOrigin });
		}

		public static void Finalizer()
		{
			ClearablePatches.SuppressItemPackets--;
		}
	}
}
