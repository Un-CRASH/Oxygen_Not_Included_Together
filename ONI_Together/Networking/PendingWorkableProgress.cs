using System.Collections.Generic;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.World;
using UnityEngine;

namespace ONI_Together.Networking
{
	// Latest state only: unresolved progress is replaceable, not a queue of work.
	internal static class PendingWorkableProgress
	{
		internal const int MaxTargets = 512;
		private const int MaxStatesPerTarget = 8;
		internal const float LifetimeSeconds = 5f;
		private static readonly Dictionary<int, Dictionary<(RemoteProgressKind, string), (WorkableProgressPacket Packet, float Received)>> pending = new();
		private static readonly HashSet<int> ready = new();
		private static readonly List<int> readySnapshot = new();
		private static readonly List<int> expiredTargets = new();
		private static readonly List<(RemoteProgressKind, string)> removedStates = new();
		private static float nextPrune;

		internal static int Count => pending.Count;

		internal static void Add(int netId, RemoteProgressKind kind, string type, WorkableProgressPacket packet)
		{
			if (netId == 0) return;
			if (!pending.TryGetValue(netId, out var states))
			{
				if (pending.Count >= MaxTargets)
				{
					Prune();
					if (pending.Count >= MaxTargets)
					{
						DebugConsole.LogAggregated("WorkableProgress.PendingFull", "[WorkableProgress] Pending target limit reached; dropping unresolved progress");
						return;
					}
				}
				states = new();
				pending.Add(netId, states);
			}
			var key = (kind, type ?? string.Empty);
			if (states.Count >= MaxStatesPerTarget && !states.ContainsKey(key)) return;
			states[key] = (packet, Time.unscaledTime);
		}

		internal static void Remove(int netId, RemoteProgressKind kind, string type)
		{
			if (!pending.TryGetValue(netId, out var states)) return;
			states.Remove((kind, type ?? string.Empty));
			if (states.Count == 0) RemoveTarget(netId);
		}

		internal static void RemoveTarget(int netId)
		{
			pending.Remove(netId);
			ready.Remove(netId);
		}

		internal static void IdentityReady(int netId)
		{
			if (pending.ContainsKey(netId)) ready.Add(netId);
		}

		internal static void FlushReady()
		{
			if (pending.Count == 0) return;
			if (!MultiplayerSession.InActiveSession || MultiplayerSession.IsHost)
			{
				Clear();
				return;
			}
			if (Time.unscaledTime >= nextPrune) Prune();

			// Apply can trigger more registrations. Consume a snapshot so those events
			// safely schedule their own pass; never scan unresolved targets each frame.
			readySnapshot.Clear();
			readySnapshot.AddRange(ready);
			ready.Clear();
			foreach (int netId in readySnapshot)
			{
				if (!pending.TryGetValue(netId, out var states)) continue;
				removedStates.Clear();
				foreach (var entry in states)
				{
					try
					{
						if (Time.unscaledTime - entry.Value.Received >= LifetimeSeconds || entry.Value.Packet.TryApply())
							removedStates.Add(entry.Key);
					}
					catch (System.Exception ex)
					{
						removedStates.Add(entry.Key);
						DebugConsole.LogAggregated("WorkableProgress.ApplyFailed", $"[WorkableProgress] Pending apply failed for {netId}: {ex.Message}");
					}
				}
				foreach (var key in removedStates) states.Remove(key);
				if (states.Count == 0) pending.Remove(netId);
			}
			readySnapshot.Clear();
		}

		private static void Prune()
		{
			float now = Time.unscaledTime;
			nextPrune = now + 1f;
			expiredTargets.Clear();
			foreach (var target in pending)
			{
				removedStates.Clear();
				foreach (var entry in target.Value)
				{
					if (now - entry.Value.Received < LifetimeSeconds) continue;
					removedStates.Add(entry.Key);
					string type = entry.Key.Item2;
					int comma = type.IndexOf(',');
					if (comma >= 0) type = type.Substring(0, comma);
					DebugConsole.LogAggregated("WorkableProgress.Unresolved." + type, $"[WorkableProgress] Expired unresolved target {target.Key} ({type})");
				}
				foreach (var key in removedStates) target.Value.Remove(key);
				if (target.Value.Count == 0) expiredTargets.Add(target.Key);
			}
			foreach (int netId in expiredTargets) RemoveTarget(netId);
		}

		internal static void Clear()
		{
			pending.Clear();
			ready.Clear();
			readySnapshot.Clear();
			expiredTargets.Clear();
			removedStates.Clear();
			nextPrune = 0f;
		}
	}
}
