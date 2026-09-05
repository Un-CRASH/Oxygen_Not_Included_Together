using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using System;
using System.Collections;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Patches.GamePatches
{
	/// <summary>
	/// Host-side cycle bookkeeping. The clock itself is no longer synced from here:
	/// GameServer.Update broadcasts it once a second of real time through
	/// GameClockSync, so it keeps flowing while the host is paused, and the client
	/// side of that class can move the clock in both directions. The OxySync
	/// GameTimeSyncer this used to attach to the GameClock never registered on any
	/// peer (no log ever shows its NetId), and GameClock.SetTime, which it relied on,
	/// can only push a clock forward.
	/// </summary>
	[HarmonyPatch(typeof(GameClock))]
	public static class GameClockPatch
	{
		private static int _lastCycle = -1;

		[HarmonyPatch(nameof(GameClock.OnPrefabInit))]
		[HarmonyPostfix]
		public static void OnPrefabInit_Postfix(GameClock __instance)
		{
			_lastCycle = __instance.GetCycle();
		}

		[HarmonyPatch(nameof(GameClock.OnDeserialized))]
		[HarmonyPostfix]
		public static void OnDeserialized_Postfix(GameClock __instance)
		{
			// Save loaded
			_lastCycle = __instance.GetCycle();
		}

		// Host: a new cycle re-arms the once-per-cycle hard sync and, when configured, runs one.
		[HarmonyPatch(nameof(GameClock.AddTime))]
		[HarmonyPostfix]
		public static void AddTime_Postfix(GameClock __instance)
		{
			using var _ = Profiler.Scope();

			try
			{
				if (!MultiplayerSession.InActiveSession || !MultiplayerSession.IsHost)
					return;

				int currentCycle = __instance.GetCycle();
				if (currentCycle == _lastCycle)
					return;

				_lastCycle = currentCycle;
				GameServerHardSync.hardSyncDoneThisCycle = false;

				bool atCycleStart = Configuration.Instance.HardSyncOnCycleStart;
				DebugConsole.Log($"[HardSync] New cycle detected ({currentCycle}); hard sync at cycle start is {(atCycleStart ? "on" : "off")}.");

				if (atCycleStart)
					CoroutineRunner.RunOne(DelayedHardSync());
			}
			catch (Exception ex)
			{
				DebugConsole.LogError($"[GameClockPatch.AddTime_Postfix] {ex}");
			}
		}

		private static IEnumerator DelayedHardSync()
		{
			using var _ = Profiler.Scope();

			yield return new WaitForSecondsRealtime(5f); // wait to ensure ONI's autosave completes (generous wait time)
			GameServerHardSync.PerformHardSync(false);
		}
	}
}
