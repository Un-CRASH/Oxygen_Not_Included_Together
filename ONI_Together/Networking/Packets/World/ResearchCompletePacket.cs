using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using System;
using System.IO;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.World
{
	/// <summary>
	/// Sent when research completes on the host to sync the completion to clients.
	/// </summary>
	public class ResearchCompletePacket : IPacket
	{
		public string TechId;

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(TechId ?? string.Empty);
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			TechId = reader.ReadString();
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (MultiplayerSession.IsHost) return;
			if (Research.Instance == null) return;
			if (string.IsNullOrEmpty(TechId)) return;

			var tech = Db.Get().Techs.TryGet(TechId);
			if (tech == null) return;

			var techInstance = Research.Instance.Get(tech);
			if (techInstance == null || techInstance.IsComplete()) return;

			try
			{
				// Mark as complete (Purchased triggers the unlocks)
				techInstance.Purchased();

				DebugConsole.Log($"[ResearchCompletePacket] Completed research: {tech.Name}");

				// Trigger the game event to notify all listeners (PlanScreen, etc.)
				// This is what normally happens when research completes on host
				try
				{
					Game.Instance?.Trigger((int)GameHashes.ResearchComplete, tech);
				}
				catch (Exception ex) { DebugConsole.LogError($"[ResearchCompletePacket] Error triggering ResearchComplete: {ex}"); }

				// Refresh the research screen if open. OnActiveResearchChanged takes the research
				// queue; the old reflective call passed no argument at all and threw a
				// TargetParameterCountException on every completed research.
				try
				{
					var researchScreen = ManagementMenu.Instance != null ? ManagementMenu.Instance.researchScreen : null;
					if (researchScreen != null)
						researchScreen.OnActiveResearchChanged(Research.Instance.GetResearchQueue());
				}
				catch (Exception ex) { DebugConsole.LogWarning($"[ResearchCompletePacket] Could not refresh the research screen: {ex.GetType().Name}: {ex.Message}"); }
			}
			catch (Exception ex)
			{
				DebugConsole.LogError($"[ResearchCompletePacket] Failed to complete research: {ex}");
			}
		}
	}
}
