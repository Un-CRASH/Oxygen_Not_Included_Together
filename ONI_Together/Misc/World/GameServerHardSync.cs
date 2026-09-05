using ONI_Together.DebugTools;
using ONI_Together.Menus;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Networking.States;
using System.Collections;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking
{
	public static class GameServerHardSync
	{
		public static bool hardSyncDoneThisCycle = false;
		private static bool hardSyncInProgress = false;
		private static int numberOfClientsAtTimeOfSync = 0;

		public static bool IsHardSyncInProgress
		{

			get
			{
				return hardSyncInProgress;
			}
			set
			{
				hardSyncInProgress = value;
			}
		}

		public static void PerformHardSync(bool consumeDailyUse = false)
		{
			using var _ = Profiler.Scope();

			if (hardSyncInProgress)
			{
				DebugConsole.Log("[HardSync] A hard sync is already in progress.");
				return;
			}

			SpeedControlScreen.Instance?.Pause(false); // Pause the game
			MultiplayerOverlay.Show(STRINGS.UI.MP_OVERLAY.SYNC.HARDSYNC_INPROGRESS);

            numberOfClientsAtTimeOfSync = MultiplayerSession.ConnectedPlayers.Count;
			var packet = new HardSyncPacket();
			PacketSender.SendToAllClients(packet);

			// Hide other player cursors as they are in hard sync and it'll reappear when they start sending packets again
			foreach (PlayerCursor cursor in MultiplayerSession.PlayerCursors.Values)
			{
				cursor.SetVisibility(false);
			}

			DebugConsole.Log($"[HardSync] Starting hard sync for {numberOfClientsAtTimeOfSync} client(s)...");
			CoroutineRunner.RunOne(HardSyncCoroutine(consumeDailyUse));
			CoroutineRunner.RunOne(ReadyWatchdog());
		}

		/// <summary>
		/// Seconds the host waits for every client to report ready before it goes on
		/// without the ones that did not. A client that had not yet processed the
		/// HardSyncPacket (it sat behind minutes of queued world traffic before the
		/// priority lane) kept the host on the waiting screen until the server was
		/// restarted by hand - and that restart wiped the identity registry
		/// (see NetworkIdentityRegistry.RebuildFromScene). The straggler still syncs
		/// when the packet reaches it; it just no longer holds everyone else.
		/// </summary>
		public const float ReadyTimeoutSeconds = 120f;

		private static IEnumerator ReadyWatchdog()
		{
			float started = Time.unscaledTime;

			// Let HardSyncCoroutine mark everyone unready before the first look.
			yield return new WaitForSecondsRealtime(2f);

			while (MultiplayerSession.IsHost && MultiplayerSession.InActiveSession && !ReadyManager.IsEveryoneReady())
			{
				if (Time.unscaledTime - started >= ReadyTimeoutSeconds)
				{
					foreach (var player in MultiplayerSession.ConnectedPlayers.Values)
					{
						if (player.PlayerId == MultiplayerSession.HostUserID) continue;
						if (player.readyState == ClientReadyState.Ready) continue;
						DebugConsole.LogWarning($"[HardSync] {player.PlayerName} ({player.PlayerId}) did not report ready within {ReadyTimeoutSeconds:F0} s; continuing without waiting for them");
						ReadyManager.SetPlayerReadyState(player, ClientReadyState.Ready);
					}
					ReadyManager.RefreshReadyState();
					yield break;
				}
				yield return new WaitForSecondsRealtime(2f);
			}
		}

		private static IEnumerator HardSyncCoroutine(bool consumeDailyUse = false)
		{
			using var _ = Profiler.Scope();

			hardSyncInProgress = true;

            ReadyManager.MarkAllAsUnready();
            SaveFileRequestPacket.SendSaveFileToAll();
            ReadyManager.RefreshScreen(); // Bring up ready screen for host

            int fileSize = SaveHelper.GetWorldSave().Length;
			int chunkSize = SaveHelper.SAVEFILE_CHUNKSIZE_KB * 1024;
			int chunkCount = Mathf.CeilToInt(fileSize / (float)chunkSize);
			float estimatedTransferDuration = chunkCount * SaveFileRequestPacket.SAVE_DATA_SEND_DELAY;
			yield return new WaitForSecondsRealtime(estimatedTransferDuration * numberOfClientsAtTimeOfSync);

			hardSyncDoneThisCycle = consumeDailyUse;
            hardSyncInProgress = false;
			// With the ready state I do not think this is needed anymore
			//SpeedControlScreen.Instance?.Unpause(false);
			//MultiplayerOverlay.Close();
		}
	}
}
