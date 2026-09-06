using ONI_Together.DebugTools;
using ONI_Together.Menus;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.OxySync.Components;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Networking.States;
using System.Collections;
using System.Collections.Generic;
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

			// Pause() is not one of the calls GameSpeedSyncer follows (only SetSpeed and
			// TogglePause are), so this pause never reached the clients: they kept
			// simulating - and the syncer kept telling them to - while the host stood
			// still. Say it explicitly; the host's play button (TogglePause) lifts it.
			GameSpeedSyncer.Instance?.RequestSetSpeed((int)GameSpeedSyncer.SpeedState.Paused);
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
			int generation = ++_generation;
			_unreadySince.Clear();
			CoroutineRunner.RunOne(HardSyncCoroutine(generation, consumeDailyUse));
			CoroutineRunner.RunOne(ReadyWatchdog(generation));
		}

		/// <summary>
		/// Seconds a client may stay unready before the host goes on without it. A client
		/// that had not yet processed the HardSyncPacket (it sat behind minutes of queued
		/// world traffic before the priority lane) kept the host on the waiting screen
		/// until the server was restarted by hand - and that restart wiped the identity
		/// registry (see NetworkIdentityRegistry.RebuildFromScene). The straggler still
		/// syncs when the packet reaches it; it just no longer holds everyone else.
		/// </summary>
		public const float ReadyTimeoutSeconds = 120f;

		/// <summary>
		/// Bumped by every PerformHardSync. The watchdog of an earlier sync exits when it
		/// sees a newer generation instead of judging the players of the sync after it.
		/// </summary>
		private static int _generation;

		// Transport IDs are reused after a disconnect. The new player must not inherit
		// the previous connection's timeout (the logs showed 120 s timeouts after 1 s).
		private static readonly Dictionary<MultiplayerPlayer, float> _unreadySince = new();

		internal static void ForgetPlayer(MultiplayerPlayer player)
		{
			if (player != null)
				_unreadySince.Remove(player);
		}

		internal static void Reset()
		{
			++_generation;
			_unreadySince.Clear();
			hardSyncInProgress = false;
			hardSyncDoneThisCycle = false;
			numberOfClientsAtTimeOfSync = 0;
		}

		/// <summary>
		/// Runs from a hard sync until the next one, so a straggler that reports Unready
		/// late - when it finally loads the save - is covered as well: the clock starts
		/// when a player is seen unready and only that player is released when it runs
		/// out. Players are not judged by the "everyone ready" flag (a player joining
		/// during the wait counts as ready by default and would have ended the watch).
		/// </summary>
		private static IEnumerator ReadyWatchdog(int generation)
		{
			while (_generation == generation && MultiplayerSession.IsHost && MultiplayerSession.InActiveSession)
			{
				// Also lets HardSyncCoroutine mark everyone unready before the first look.
				yield return new WaitForSecondsRealtime(2f);
				if (_generation != generation || !MultiplayerSession.IsHost || !MultiplayerSession.InActiveSession)
					yield break;

				float now = Time.unscaledTime;
				bool released = false;
				foreach (var player in MultiplayerSession.ConnectedPlayers.Values)
				{
					if (player.PlayerId == MultiplayerSession.HostUserID) continue;
					if (player.readyState == ClientReadyState.Ready)
					{
						_unreadySince.Remove(player);
						continue;
					}
					if (!_unreadySince.TryGetValue(player, out float since))
					{
						_unreadySince[player] = now;
						continue;
					}
					if (now - since < ReadyTimeoutSeconds) continue;

					DebugConsole.LogWarning($"[HardSync] {player.PlayerName} ({player.PlayerId}) did not report ready within {ReadyTimeoutSeconds:F0} s; continuing without waiting for them");
					ReadyManager.SetPlayerReadyState(player, ClientReadyState.Ready);
					_unreadySince.Remove(player);
					released = true;
				}

				if (released)
					ReadyManager.RefreshReadyState();
			}
		}

		private static IEnumerator HardSyncCoroutine(int generation, bool consumeDailyUse = false)
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
			if (_generation != generation) yield break;

			hardSyncDoneThisCycle = consumeDailyUse;
            hardSyncInProgress = false;
			// With the ready state I do not think this is needed anymore
			//SpeedControlScreen.Instance?.Unpause(false);
			//MultiplayerOverlay.Close();
		}
	}
}
