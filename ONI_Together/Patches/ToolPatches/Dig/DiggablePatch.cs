using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using Shared.Profiling;

namespace ONI_Together.Patches.ToolPatches.Dig
{
	[HarmonyPatch(typeof(Diggable), "OnStopWork")]
	public static class DiggablePatch
	{
		public static void Prefix(Diggable __instance)
		{
			using var _ = Profiler.Scope();

			if (!MultiplayerSession.IsHost || !MultiplayerSession.InActiveSession)
				return;

			// Workable.StopWork runs OnStopWork for every interruption as well as for
			// completion: a duplicant re-tasked mid-dig, a lost chore, a pause. Only the
			// completed dig (isDigComplete, the flag Diggable itself destroys the placer
			// on) may tell the clients to destroy the cell; the packet for an interrupted
			// one removed a tile on the client that still stood on the host.
			if (!__instance.isDigComplete)
				return;

			int cell = __instance.GetCell();

			if (!Grid.IsValidCell(cell))
				return;

			var packet = new DigCompletePacket
			{
				Cell = cell,
				Mass = Grid.Mass[cell],
				Temperature = Grid.Temperature[cell],
				ElementIdx = Grid.ElementIdx[cell],
				DiseaseIdx = Grid.DiseaseIdx[cell],
				DiseaseCount = Grid.DiseaseCount[cell]
			};

			PacketSender.SendToAllClients(packet);
			DebugConsole.Log($"[DigComplete] Host sent DigCompletePacket for cell {cell}");
		}
	}
}
